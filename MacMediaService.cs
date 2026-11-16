using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kashishu;

/// <summary>
/// Local playback source on macOS. macOS has no public MPRIS counterpart and the private
/// MediaRemote now-playing API is not available to third-party apps on current releases, so
/// the two scriptable players are queried through a persistent AppleScript process: Spotify
/// and Music. The bridge stays alive between polls and answers one JSON line per request.
/// </summary>
internal sealed class MacMediaService : IPlaybackSource
{
    private const long PositionJitterToleranceMs = 1_000;
    private const int MaximumArtworkBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(15);
    private static readonly Lazy<string> BridgeScript = new(LoadBridgeScript);

    private readonly PlaybackStore _store;
    private readonly SemaphoreSlim _pollWake = new(0);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);
    private Player? _player;
    private string? _selectedSource;
    private TrackInfo? _track;
    private TrackInfo? _loadingTrack;
    private CancellationTokenSource? _trackLoad;
    private int _lyricsSource;
    private int _refreshCurrentTrack;
    private bool _forcePositionSync;
    private Process? _bridge;
    private Channel<string>? _bridgeResponses;

    public MacMediaService(PlaybackStore store, LyricsSourcePreference lyricsSource)
    {
        _store = store;
        _lyricsSource = (int)lyricsSource;
    }

    public void SetLyricsSource(LyricsSourcePreference source) =>
        Volatile.Write(ref _lyricsSource, (int)source);

    public bool RequestCurrentTrackRefresh()
    {
        if (_store.Snapshot.Track is null)
            return false;
        if (Interlocked.Exchange(ref _refreshCurrentTrack, 1) == 0)
            _pollWake.Release();
        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = PollInterval;
                try
                {
                    await _gate.WaitAsync(cancellationToken);
                    try { await PollAsync(cancellationToken); }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetStatus($"本地播放器 连接失败：{Friendly(exception)}");
                    StopBridge();
                    delay = TimeSpan.FromSeconds(2);
                }

                try
                {
                    _ = await _pollWake.WaitAsync(delay, cancellationToken);
                    while (_pollWake.Wait(0)) { }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            StopBridge();
            CancelTrackLoad();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var payload = await BridgeRequestAsync("poll", cancellationToken);
        using var document = JsonDocument.Parse(payload);
        var players = new List<Player>(2);
        var authorizationDenied = false;
        foreach (var name in new[] { "spotify", "music" })
        {
            if (!document.RootElement.TryGetProperty(name, out var element)
                || element.ValueKind != JsonValueKind.Object)
                continue;
            var error = String(element, "error");
            if (error is not null && IsAuthorizationError(error))
                authorizationDenied = true;
            if (ParsePlayer(element) is { } parsed)
                players.Add(parsed);
        }

        var player = SelectPlayer(players, _selectedSource);
        _player = player;
        if (player is null)
        {
            ClearTrack();
            _store.Update(new PlaybackSnapshot(
                null,
                0,
                Stopwatch.GetTimestamp(),
                false,
                authorizationDenied
                    ? "本地播放器 需要授权：请在系统设置中允许控制 Spotify 或 Music"
                    : PlaybackStore.NoActiveMacPlaybackStatus));
            return;
        }

        var reportedAt = Stopwatch.GetTimestamp();
        var previous = _store.Snapshot;
        var trackId = BuildTrackId(player);
        var positionMs = player.PositionMs;
        var samePlayer = _selectedSource == player.Source;
        if (!_forcePositionSync && samePlayer && previous.Track?.Id == trackId
            && previous.IsPlaying && player.IsPlaying && previous.PlaybackRate == 1d)
        {
            var predicted = (long)previous.PositionMs;
            if (Math.Abs(positionMs - predicted) <= PositionJitterToleranceMs)
                positionMs = predicted;
        }
        _forcePositionSync = false;
        _selectedSource = player.Source;
        if (player.DurationMs > 0)
            positionMs = Math.Min(positionMs, player.DurationMs);

        var source = new SourceTrack(
            trackId,
            player.Title,
            player.Artists,
            [],
            player.Artists,
            player.Album,
            player.DurationMs,
            null,
            player.ArtworkUrl);

        var forceRefresh = Interlocked.Exchange(ref _refreshCurrentTrack, 0) != 0;
        var current = Volatile.Read(ref _track);
        if (forceRefresh || !SameMetadata(current, source))
        {
            CancelTrackLoad();
            var reusable = current?.Id == trackId ? current : TrackCache.Load(trackId);
            var track = SameMetadata(reusable, source)
                ? reusable!
                : new TrackInfo(
                    trackId,
                    source.Title,
                    source.Artists,
                    source.Album,
                    source.DurationMs,
                    [],
                    []);
            if (track.AlbumArtBytes.IsDefaultOrEmpty && !player.ArtworkBytes.IsDefaultOrEmpty)
                track = track with { AlbumArtBytes = player.ArtworkBytes };
            if (player.ArtworkUrl is { Length: > 0 } artworkUrl)
                track = track with { AlbumArtUrl = artworkUrl };
            track = track with { ShareUrl = ShareUrl(player) };
            Volatile.Write(ref _track, track);
            Volatile.Write(ref _loadingTrack, track);
            _store.Update(Snapshot(player, track, positionMs, reportedAt));
            _trackLoad = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = CompleteTrackAsync(source, player, track, _trackLoad.Token);
        }

        var displayed = Volatile.Read(ref _track)!;
        _store.Update(Snapshot(player, displayed, positionMs, reportedAt));
    }

    private PlaybackSnapshot Snapshot(Player player, TrackInfo track, long positionMs, long reportedAt)
    {
        var status = track.Lyrics.IsDefaultOrEmpty
            ? ReferenceEquals(track, Volatile.Read(ref _loadingTrack))
                ? "正在获取歌词…"
                : $"未找到歌词 · {string.Join(", ", track.Artists)}"
            : string.Join(", ", track.Artists);
        return new PlaybackSnapshot(
            track, positionMs, reportedAt, player.IsPlaying, status, 1d)
        {
            Controls = player.Controls
        };
    }

    public async Task<string?> ControlAsync(PlaybackCommand command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Refresh the metadata and the selected player together before validating the
            // track ID, so an old song's seek cannot be sent to another player.
            await PollAsync(cancellationToken);
            var player = _player;
            if (player is null)
                return "没有可控制的本地播放器";
            var track = Volatile.Read(ref _track);
            if (track is null)
                return "当前没有可控制的歌曲";
            if (command.TrackId is { } id && id != track.Id)
                return "歌曲已切换，请重试";

            var controls = player.Controls;
            var allowed = command.Action switch
            {
                PlaybackAction.PlayPause => controls.CanPlayPause,
                PlaybackAction.Previous => controls.CanPrevious,
                PlaybackAction.Next => controls.CanNext,
                PlaybackAction.Seek => controls.CanSeek,
                PlaybackAction.Shuffle => controls.Shuffle.HasValue,
                PlaybackAction.Repeat => controls.Repeat is not null,
                PlaybackAction.Volume => controls.Volume.HasValue,
                PlaybackAction.Favorite => controls.CanFavorite,
                _ => false
            };
            if (!allowed)
                return "当前播放器不支持此操作";

            var request = command.Action switch
            {
                PlaybackAction.PlayPause => Command(player.Source, "playpause"),
                PlaybackAction.Previous => Command(player.Source, "previous"),
                PlaybackAction.Next => Command(player.Source, "next"),
                PlaybackAction.Seek => Command(player.Source, "seek",
                    Math.Clamp(command.Value, 0, Math.Max(0, player.DurationMs)) / 1000d),
                PlaybackAction.Shuffle => Command(player.Source, "shuffle", command.Value > 0),
                PlaybackAction.Repeat => Command(player.Source, "repeat", command.Value switch
                {
                    1 => "context",
                    2 => "track",
                    _ => "off"
                }),
                PlaybackAction.Volume => Command(player.Source, "volume",
                    Math.Clamp(command.Value, 0, 1)),
                PlaybackAction.Favorite => Command(player.Source, "favorite", command.Value > 0),
                _ => throw new InvalidOperationException()
            };
            var response = await BridgeRequestAsync(request, cancellationToken);
            using (var document = JsonDocument.Parse(response))
            {
                if (!(document.RootElement.TryGetProperty("ok", out var ok)
                        && ok.ValueKind == JsonValueKind.True))
                {
                    var error = String(document.RootElement, "error");
                    return error is not null && IsAuthorizationError(error)
                        ? "需要允许 Kashishu 控制 Spotify 或 Music"
                        : "当前播放器不支持此操作";
                }
            }
            if (command.Action is PlaybackAction.Seek or PlaybackAction.PlayPause)
                _forcePositionSync = true;
            await PollAsync(cancellationToken);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return $"播放控制失败：{Friendly(exception)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private static readonly JsonSerializerOptions BridgeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string Command(string source, string action, object? value = null)
    {
        var command = new BridgeCommand(source, action, value);
        return "cmd " + JsonSerializer.Serialize(command, BridgeJson);
    }

    private sealed record BridgeCommand(string Source, string Action, object? Value);

    internal sealed record Player(
        string Source,
        string Title,
        ImmutableArray<string> Artists,
        string Album,
        long DurationMs,
        long PositionMs,
        bool IsPlaying,
        string? TrackId,
        string? ArtworkUrl,
        ImmutableArray<byte> ArtworkBytes,
        PlaybackControls Controls);

    internal static string? ShareUrl(Player player)
    {
        // Music's persistent/database IDs are local library identities, not public links.
        const string prefix = "spotify:track:";
        if (player.Source == "spotify" && player.TrackId is { } id
            && id.StartsWith(prefix, StringComparison.Ordinal)
            && id[prefix.Length..] is { Length: 22 } value && value.All(char.IsAsciiLetterOrDigit))
            return $"https://open.spotify.com/track/{value}";
        return null;
    }

    internal static Player? ParsePlayer(JsonElement element)
    {
        var source = String(element, "source");
        var state = String(element, "state");
        var title = String(element, "title")?.Trim();
        if (source is not ("spotify" or "music")
            || string.IsNullOrEmpty(title)
            || state is not ("playing" or "paused" or "fast forwarding" or "rewinding"))
            return null;

        var artists = SplitArtists(String(element, "artist"));
        var album = String(element, "album")?.Trim() ?? string.Empty;
        var durationMs = Number(element, "durationMs");
        var positionMs = Number(element, "positionMs");
        var isPlaying = state == "playing" || state == "fast forwarding" || state == "rewinding";
        var repeat = String(element, "repeat") is "off" or "context" or "track"
            ? String(element, "repeat")
            : null;
        var shuffle = Boolean(element, "shuffle");
        var canFavorite = Boolean(element, "canFavorite") == true;
        var favorite = Boolean(element, "favorite");
        var volume = Ratio(element, "volume");
        return new Player(
            source,
            title,
            artists,
            album,
            durationMs,
            positionMs,
            isPlaying,
            String(element, "trackId"),
            String(element, "artworkUrl"),
            DecodeArtwork(String(element, "artworkBase64")),
            new PlaybackControls(
                CanPlayPause: true,
                CanPrevious: true,
                CanNext: true,
                CanSeek: durationMs > 0,
                Shuffle: shuffle,
                Repeat: repeat,
                Volume: volume is { } value ? Math.Clamp(value, 0, 1) : null,
                CanFavorite: canFavorite,
                IsFavorite: canFavorite ? favorite : null));
    }

    /// <summary>Chooses the playing player, keeping the previous one when both are paused and
    /// falling back to a stable order so the selection does not flip between polls.</summary>
    internal static Player? SelectPlayer(IReadOnlyList<Player> players, string? selectedSource) =>
        players
            .OrderBy(player => player.IsPlaying ? 0 : 1)
            .ThenBy(player => player.Source == selectedSource ? 0 : 1)
            .ThenBy(player => player.Source, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool SameMetadata(TrackInfo? current, SourceTrack source) =>
        current?.Id == source.Id
        && current.Title == source.Title
        && current.Artists.SequenceEqual(source.Artists)
        && current.Album == source.Album
        && current.DurationMs == source.DurationMs;

    internal static string BuildTrackId(Player player)
    {
        var identity = string.IsNullOrWhiteSpace(player.TrackId) || player.TrackId == "0"
            ? string.Join('\n', player.Source, player.Title, string.Join('\u001f', player.Artists),
                player.Album, player.DurationMs)
            : $"{player.Source}\n{player.TrackId}";
        return "mac:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static ImmutableArray<string> SplitArtists(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([", ", "、", " / ", " & "], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries).ToImmutableArray();

    private static ImmutableArray<byte> DecodeArtwork(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return [];
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length is 0 or > MaximumArtworkBytes ? [] : bytes.ToImmutableArray();
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private async Task CompleteTrackAsync(
        SourceTrack source,
        Player player,
        TrackInfo placeholder,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyricsSource = (LyricsSourcePreference)Volatile.Read(ref _lyricsSource);
            var lyricsTask = LoadLyricsSafeAsync(source, lyricsSource, cancellationToken);
            var albumTask = player.ArtworkUrl is { Length: > 0 } url
                ? SpotifyService.LoadImageSafeAsync(url, cancellationToken)
                : Task.FromResult(ImmutableArray<byte>.Empty);
            await Task.WhenAll(lyricsTask, albumTask);
            var lyrics = await lyricsTask;
            var album = await albumTask;
            var next = placeholder with
            {
                Title = source.Title,
                Artists = source.Artists,
                Album = source.Album,
                DurationMs = source.DurationMs,
                AlbumArtBytes = TrackCache.PreferLargerImage(placeholder.AlbumArtBytes, album),
                Lyrics = lyrics.IsEmpty ? placeholder.Lyrics : lyrics,
                LyricsSource = lyrics.IsEmpty ? placeholder.LyricsSource : lyricsSource
            };
            if (Interlocked.CompareExchange(ref _track, next, placeholder) == placeholder)
                TrackCache.SaveIfChanged(next);
            Interlocked.CompareExchange(ref _loadingTrack, null, placeholder);
            _pollWake.Release();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[track] load failed: {Friendly(exception)}");
            Interlocked.CompareExchange(ref _loadingTrack, null, placeholder);
            _pollWake.Release();
        }
    }

    private static async Task<ImmutableArray<LyricLine>> LoadLyricsSafeAsync(
        SourceTrack track,
        LyricsSourcePreference source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LyricsProvider.LoadAsync(track, source, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private void CancelTrackLoad()
    {
        Volatile.Write(ref _loadingTrack, null);
        _trackLoad?.Cancel();
        _trackLoad?.Dispose();
        _trackLoad = null;
    }

    private void ClearTrack()
    {
        Volatile.Write(ref _track, null);
        CancelTrackLoad();
        _selectedSource = null;
    }

    private void SetStatus(string status) =>
        _store.Update(_store.Snapshot with { Status = status });

    private static string Friendly(Exception exception) =>
        exception is AggregateException { InnerException: { } inner }
            ? inner.Message
            : exception.Message;

    private static bool IsAuthorizationError(string message) =>
        message.Contains("-1743", StringComparison.Ordinal)
        || message.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)
        || message.Contains("未获授权", StringComparison.Ordinal)
        || message.Contains("不允许", StringComparison.Ordinal);

    private static string? String(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool? Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is
            JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? (long)Math.Max(0, number)
            : 0;

    private static double? Ratio(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? number
            : null;

    private static string LoadBridgeScript()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Kashishu.MacMediaBridge.js")
            ?? throw new InvalidOperationException("Embedded macOS media bridge is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task<string> BridgeRequestAsync(string request, CancellationToken cancellationToken)
    {
        await _bridgeGate.WaitAsync(cancellationToken);
        try
        {
            var responses = await EnsureBridgeAsync(cancellationToken);
            var process = _bridge
                ?? throw new InvalidOperationException("AppleScript 进程未启动");
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BridgeTimeout);
            try
            {
                return await responses.Reader.ReadAsync(timeout.Token);
            }
            catch
            {
                StopBridge();
                throw;
            }
        }
        finally
        {
            _bridgeGate.Release();
        }
    }

    private async Task<Channel<string>> EnsureBridgeAsync(CancellationToken cancellationToken)
    {
        if (_bridge is { HasExited: false } && _bridgeResponses is { } existing)
            return existing;

        StopBridge();
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add("JavaScript");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(BridgeScript.Value);
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 AppleScript 进程");
        var responses = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _bridge = process;
        _bridgeResponses = responses;
        _ = Task.Run(() => ReadBridgeOutputAsync(process, responses));
        _ = Task.Run(() => ReadBridgeErrorAsync(process));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BridgeTimeout);
        string ready;
        try
        {
            ready = await responses.Reader.ReadAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            StopBridge();
            throw new InvalidOperationException("AppleScript 进程未能就绪", exception);
        }
        if (ready.Trim() != "ready")
        {
            StopBridge();
            throw new InvalidOperationException("AppleScript 进程未能就绪");
        }
        return responses;
    }

    private static async Task ReadBridgeOutputAsync(Process process, Channel<string> responses)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!responses.Writer.TryWrite(line))
                    break;
            }
        }
        catch (Exception)
        {
            // The process was stopped while a read was pending.
        }
        finally
        {
            responses.Writer.TryComplete();
        }
    }

    private static async Task ReadBridgeErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Console.Error.WriteLine($"[mac] media bridge: {line}");
            }
        }
        catch (Exception)
        {
            // The process was stopped while a read was pending.
        }
    }

    private void StopBridge()
    {
        var process = Interlocked.Exchange(ref _bridge, null);
        var responses = Interlocked.Exchange(ref _bridgeResponses, null);
        responses?.Writer.TryComplete();
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("quit");
                process.StandardInput.Flush();
            }
        }
        catch (Exception)
        {
            // The process is already gone.
        }
        try
        {
            if (!process.WaitForExit(500))
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process is already gone.
        }
        process.Dispose();
    }
}
