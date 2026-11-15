#if WINDOWS
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LyricifyIsland;

/// <summary>
/// Local playback source on Windows. MPRIS has no equivalent here, so the system media
/// transport controls (the same session the volume flyout shows) provide metadata and
/// playback commands for every player that integrates with Windows.
/// </summary>
internal sealed class WindowsMediaService : IPlaybackSource
{
    private const long PositionJitterToleranceMs = 1_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    private readonly PlaybackStore _store;
    private readonly SemaphoreSlim _pollWake = new(0);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TrackInfo? _track;
    private TrackInfo? _loadingTrack;
    private CancellationTokenSource? _trackLoad;
    private int _lyricsSource;
    private int _refreshCurrentTrack;
    private bool _forcePositionSync;

    public WindowsMediaService(PlaybackStore store, LyricsSourcePreference lyricsSource)
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
                SetStatus($"本地媒体会话 连接失败：{Friendly(exception)}");
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
        CancelTrackLoad();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var manager = await ManagerAsync(cancellationToken);
        var session = SelectSession(manager);
        _session = session;
        if (session is null)
        {
            ClearTrack();
            _store.Update(new PlaybackSnapshot(
                null, 0, Stopwatch.GetTimestamp(), false,
                PlatformInfo.NoActiveLocalPlaybackStatus));
            return;
        }

        var media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        if (string.IsNullOrWhiteSpace(media.Title))
        {
            ClearTrack();
            _store.Update(new PlaybackSnapshot(null, 0, Stopwatch.GetTimestamp(), false,
                PlatformInfo.NoActiveLocalPlaybackStatus));
            return;
        }
        var info = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var controls = ReadControls(info);
        var reportedAt = Stopwatch.GetTimestamp();
        var isPlaying = info.PlaybackStatus
            == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var playbackRate = info.PlaybackRate is { } rate && double.IsFinite(rate) && rate > 0 ? rate : 1d;
        var durationMs = Math.Max(0,
            (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds);
        controls = controls with { CanSeek = controls.CanSeek && durationMs > 0 };
        var positionMs = CurrentPosition(timeline.Position, timeline.StartTime,
            timeline.LastUpdatedTime, DateTimeOffset.UtcNow, isPlaying, playbackRate);
        if (durationMs > 0)
            positionMs = Math.Min(positionMs, durationMs);

        var artists = SplitArtists(media.Artist);
        var albumArtists = SplitArtists(media.AlbumArtist);
        if (albumArtists.IsDefaultOrEmpty)
            albumArtists = artists;
        var trackId = BuildTrackId(session.SourceAppUserModelId,
            media.Title, media.Artist, media.AlbumTitle, durationMs);
        var source = new SourceTrack(
            trackId,
            media.Title,
            artists,
            [],
            albumArtists,
            media.AlbumTitle ?? string.Empty,
            durationMs,
            null,
            null);

        var previous = _store.Snapshot;
        if (!_forcePositionSync && previous.Track?.Id == trackId
            && previous.IsPlaying && isPlaying && previous.PlaybackRate == playbackRate)
        {
            var predicted = (long)previous.PositionMs;
            if (Math.Abs(positionMs - predicted) <= PositionJitterToleranceMs)
                positionMs = predicted;
        }
        _forcePositionSync = false;

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
            if (track.AlbumArtBytes.IsDefaultOrEmpty)
                track = track with { AlbumArtBytes = await ReadThumbnailAsync(media.Thumbnail, cancellationToken) };
            Volatile.Write(ref _track, track);
            Volatile.Write(ref _loadingTrack, track);
            _store.Update(Snapshot(isPlaying, playbackRate, positionMs, reportedAt, track, controls));
            _trackLoad = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = CompleteTrackAsync(source, track, _trackLoad.Token);
        }

        var displayed = Volatile.Read(ref _track)!;
        _store.Update(Snapshot(isPlaying, playbackRate, positionMs, reportedAt, displayed, controls));
    }

    internal static long CurrentPosition(TimeSpan position, TimeSpan start,
        DateTimeOffset updatedAt, DateTimeOffset now, bool isPlaying, double playbackRate)
    {
        var milliseconds = (position - start).TotalMilliseconds;
        // Players may publish the timeline only on a seek or pause. Keep extrapolating
        // from its timestamp for the entire interval, including playback at another rate.
        if (isPlaying && updatedAt != default)
            milliseconds += Math.Max(0, (now - updatedAt).TotalMilliseconds) * playbackRate;
        return (long)Math.Max(0, milliseconds);
    }

    private static GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions.Count == 0)
            return null;
        var current = manager.GetCurrentSession();
        if (current is not null && IsPlaying(current))
            return current;
        foreach (var session in sessions)
        {
            if (IsPlaying(session))
                return session;
        }
        return current ?? sessions[0];
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            // A session can disappear while it is being inspected.
            return false;
        }
    }

    private PlaybackSnapshot Snapshot(
        bool isPlaying, double playbackRate, long positionMs, long reportedAt,
        TrackInfo track, PlaybackControls controls)
    {
        var status = track.Lyrics.IsDefaultOrEmpty
            ? ReferenceEquals(track, Volatile.Read(ref _loadingTrack))
                ? "正在获取歌词…"
                : $"未找到歌词 · {string.Join(", ", track.Artists)}"
            : string.Join(", ", track.Artists);
        return new PlaybackSnapshot(track, positionMs, reportedAt, isPlaying, status, playbackRate)
        {
            Controls = controls
        };
    }

    private static PlaybackControls ReadControls(
        GlobalSystemMediaTransportControlsSessionPlaybackInfo info)
    {
        try
        {
            var controls = info.Controls;
            return new PlaybackControls(
                CanPlayPause: controls.IsPlayPauseToggleEnabled
                    || (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                CanPrevious: controls.IsPreviousEnabled,
                CanNext: controls.IsNextEnabled,
                CanSeek: controls.IsPlaybackPositionEnabled,
                Shuffle: controls.IsShuffleEnabled ? info.IsShuffleActive : null,
                Repeat: controls.IsRepeatEnabled && info.AutoRepeatMode is { } repeat
                    ? RepeatName(repeat)
                    : null,
                Volume: null,
                CanFavorite: false,
                IsFavorite: null);
        }
        catch (Exception)
        {
            return new PlaybackControls();
        }
    }

    public async Task<string?> ControlAsync(
        PlaybackCommand command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Refresh the metadata and session together before validating the UI's track ID.
            // Re-selecting only the session could send an old song's seek to another player.
            await PollAsync(cancellationToken);
            var session = _session;
            if (session is null)
                return "没有可控制的本地媒体会话";
            if (Volatile.Read(ref _track) is null)
                return "当前没有可控制的歌曲";
            if (command.TrackId is { } id && id != Volatile.Read(ref _track)?.Id)
                return "歌曲已切换，请重试";

            var ok = command.Action switch
            {
                PlaybackAction.PlayPause => await TogglePlayPauseAsync(session, cancellationToken),
                PlaybackAction.Previous => await session.TrySkipPreviousAsync()
                    .AsTask(cancellationToken),
                PlaybackAction.Next => await session.TrySkipNextAsync()
                    .AsTask(cancellationToken),
                PlaybackAction.Seek => await session.TryChangePlaybackPositionAsync(
                        SeekPosition(command.Value, session.GetTimelineProperties()))
                    .AsTask(cancellationToken),
                PlaybackAction.Shuffle => await session.TryChangeShuffleActiveAsync(command.Value > 0)
                    .AsTask(cancellationToken),
                PlaybackAction.Repeat => await session.TryChangeAutoRepeatModeAsync(
                        command.Value switch
                        {
                            1 => MediaPlaybackAutoRepeatMode.List,
                            2 => MediaPlaybackAutoRepeatMode.Track,
                            _ => MediaPlaybackAutoRepeatMode.None
                        })
                    .AsTask(cancellationToken),
                _ => false
            };
            if (!ok)
                return "当前播放器不支持此操作";
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

    private async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync(CancellationToken token) =>
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);

    private static async Task<bool> TogglePlayPauseAsync(
        GlobalSystemMediaTransportControlsSession session, CancellationToken token)
    {
        var info = session.GetPlaybackInfo();
        if (info.Controls.IsPlayPauseToggleEnabled)
            return await session.TryTogglePlayPauseAsync().AsTask(token);
        return info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? await session.TryPauseAsync().AsTask(token)
            : await session.TryPlayAsync().AsTask(token);
    }

    private static long SeekPosition(double milliseconds,
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline) =>
        SeekPosition(milliseconds, timeline.StartTime, timeline.EndTime);

    internal static long SeekPosition(double milliseconds, TimeSpan start, TimeSpan end) =>
        start.Ticks + (long)(Math.Clamp(milliseconds, 0,
            Math.Max(0, (end - start).TotalMilliseconds)) * TimeSpan.TicksPerMillisecond);

    private async Task<ImmutableArray<byte>> ReadThumbnailAsync(
        IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail is null)
            return [];
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken);
            if (stream.Size is 0 or > 16 * 1024 * 1024)
                return [];
            using var memory = new MemoryStream((int)stream.Size);
            await stream.AsStreamForRead().CopyToAsync(memory, cancellationToken);
            return memory.ToArray().ToImmutableArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[windows] media thumbnail failed: {exception.Message}");
            return [];
        }
    }

    private async Task CompleteTrackAsync(
        SourceTrack source, TrackInfo placeholder, CancellationToken cancellationToken)
    {
        try
        {
            var lyricsSource = (LyricsSourcePreference)Volatile.Read(ref _lyricsSource);
            var lyrics = await LoadLyricsSafeAsync(source, lyricsSource, cancellationToken);
            var next = placeholder with
            {
                Title = source.Title,
                Artists = source.Artists,
                Album = source.Album,
                DurationMs = source.DurationMs,
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
        SourceTrack track, LyricsSourcePreference source, CancellationToken cancellationToken)
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
    }

    private void SetStatus(string status)
    {
        _store.Update(_store.Snapshot with { Status = status });
    }

    private static bool SameMetadata(TrackInfo? current, SourceTrack source) =>
        current?.Id == source.Id
        && current.Title == source.Title
        && current.Artists.SequenceEqual(source.Artists)
        && current.Album == source.Album
        && current.DurationMs == source.DurationMs;

    private static string BuildTrackId(
        string? app, string title, string artist, string album, long durationMs) =>
        "smtc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{app}\n{title}\n{artist}\n{album}\n{durationMs}")));

    private static ImmutableArray<string> SplitArtists(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([", ", "、", " / "], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries).ToImmutableArray();

    internal static string RepeatName(MediaPlaybackAutoRepeatMode mode) => mode switch
    {
        MediaPlaybackAutoRepeatMode.Track => "track",
        MediaPlaybackAutoRepeatMode.List => "context",
        _ => "off"
    };

    private static string Friendly(Exception exception) =>
        exception is AggregateException { InnerException: { } inner }
            ? inner.Message
            : exception.Message;
}
#endif
