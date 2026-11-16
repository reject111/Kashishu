using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Avalonia.Threading;
using Avalonia.Input.Platform;

namespace Kashishu;

/// <summary>
/// Owns the native AppKit/Swift island process on macOS. The helper only knows how to draw and
/// interact with the capsule; playback, lyrics, settings and persistence stay in the existing
/// application, which keeps platform services shared with the Avalonia island.
/// </summary>
internal sealed class MacGlassIsland : IDisposable
{
    private const string HelperName = "KashishuGlass";
    private const int ProtocolVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PlaybackStore _store;
    private readonly Func<PlaybackCommand, CancellationToken, Task<string?>> _controlPlayback;
    private readonly Action _showLyricsWindow;
    private readonly Action _showBanner;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly Func<TrackInfo, int, bool> _trackOffsetChanged;
    private readonly Func<IslandSettings, bool> _applySettings;
    private readonly Action _failed;
    private readonly CancellationTokenSource _lifetime = new();
    private IslandSettings _settings;
    private Process? _process;
    private Channel<string>? _messages;
    private CancellationTokenSource? _writerCancellation;
    private DispatcherTimer? _playbackTimer;
    private TaskCompletionSource<bool>? _ready;
    private TrackInfo? _sentTrack;
    private bool _sentApplicationIcon;
    private bool _busy;
    private bool _running;
    private bool _stopping;
    private bool _disposed;
    private bool _failurePosted;

    public MacGlassIsland(
        PlaybackStore store,
        IslandSettings settings,
        Func<PlaybackCommand, CancellationToken, Task<string?>> controlPlayback,
        Action showLyricsWindow,
        Action showBanner,
        Action showSettings,
        Action exit,
        Func<TrackInfo, int, bool> trackOffsetChanged,
        Func<IslandSettings, bool> applySettings,
        Action failed)
    {
        _store = store;
        _settings = SettingsStore.Normalize(settings);
        _controlPlayback = controlPlayback;
        _showLyricsWindow = showLyricsWindow;
        _showBanner = showBanner;
        _showSettings = showSettings;
        _exit = exit;
        _trackOffsetChanged = trackOffsetChanged;
        _applySettings = applySettings;
        _failed = failed;
    }

    internal bool IsRunning => _running;

    internal bool Start()
    {
        if (_disposed || _running)
            return !_disposed;
        if (!OperatingSystem.IsMacOS())
            return false;
        if (FindHelper() is not { } helper)
        {
            Console.Error.WriteLine($"[macos-glass] {HelperName} was not found next to the application.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helper)!
            };
            startInfo.Environment["KASHISHU_NATIVE_ISLAND"] = "1";
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnError;
            process.Exited += OnExited;
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }

            _process = process;
            var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = true;
            _stopping = false;
            _failurePosted = false;
            _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = WriteMessagesAsync(process, input, _messages, _writerCancellation.Token);
            _sentTrack = null;
            _sentApplicationIcon = false;
            _store.Changed += OnStoreChanged;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var ready = _ready.Task.Wait(TimeSpan.FromSeconds(2)) && _ready.Task.Result;
            _ready = null;
            if (!ready || process.HasExited)
            {
                Console.Error.WriteLine("[macos-glass] helper did not complete the ready handshake.");
                StopProcess();
                return false;
            }

            _playbackTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                (_, _) => SendPlayback());
            _playbackTimer.Start();
            Send(new { type = "hello", protocolVersion = ProtocolVersion });
            SendApplicationIcon();
            SendSettings();
            SendTrack(_store.Snapshot.Track, force: true);
            SendPlayback();
            Console.Error.WriteLine($"[macos-glass] started {helper}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[macos-glass] start failed: {exception.Message}");
            StopProcess();
            return false;
        }
    }

    internal void ApplySettings(IslandSettings settings)
    {
        var previous = _settings;
        _settings = SettingsStore.Normalize(settings);
        if (_running)
        {
            if (previous.EnglishWordEffects != _settings.EnglishWordEffects)
                SendTrack(_store.Snapshot.Track, force: true);
            SendSettings();
            SendPlayback();
        }
    }

    private static string? FindHelper()
    {
        var overridePath = Environment.GetEnvironmentVariable("KASHISHU_MACOS_GLASS_HELPER");
        if (File.Exists(overridePath))
            return Path.GetFullPath(overridePath!);

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, HelperName),
            Path.Combine(baseDirectory, "Helpers", HelperName),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Helpers", HelperName)),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", HelperName))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void OnStoreChanged(object? sender, EventArgs args)
    {
        if (_disposed || !_running)
            return;
        if (Dispatcher.UIThread.CheckAccess())
            SendPlayback();
        else
            Dispatcher.UIThread.Post(SendPlayback);
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
            return;
        try
        {
            using var document = JsonDocument.Parse(args.Data);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "ready")
            {
                var version = root.TryGetProperty("protocolVersion", out var versionElement)
                    ? versionElement.GetInt32() : 0;
                if (version != ProtocolVersion)
                {
                    Console.Error.WriteLine($"[macos-glass] protocol mismatch: helper={version}, app={ProtocolVersion}");
                    _ready?.TrySetResult(false);
                    return;
                }
                _ready?.TrySetResult(true);
            }
            else if (type == "action")
            {
                var message = root.Clone();
                Dispatcher.UIThread.Post(() =>
                {
                    try { HandleAction(message); }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException
                        or FormatException or OverflowException or KeyNotFoundException)
                    {
                        Console.Error.WriteLine($"[macos-glass] invalid action: {exception.Message}");
                    }
                });
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException)
        {
            Console.Error.WriteLine($"[macos-glass] invalid helper message: {exception.Message}");
        }
    }

    private void OnError(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
            Console.Error.WriteLine(args.Data);
    }

    private void OnExited(object? sender, EventArgs args)
    {
        _ready?.TrySetResult(false);
        if (_stopping || _disposed)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_stopping || _disposed)
                return;
            Console.Error.WriteLine("[macos-glass] helper exited; restoring the Avalonia island.");
            StopProcess();
            _failed();
        });
    }

    private void HandleAction(JsonElement root)
    {
        if (_disposed || !_running)
            return;
        var action = root.TryGetProperty("action", out var actionElement)
            ? actionElement.GetString() : null;
        var trackId = root.TryGetProperty("trackId", out var trackElement)
            ? trackElement.GetString() : null;
        switch (action)
        {
            case "playPause":
                _ = SendPlaybackAsync(PlaybackAction.PlayPause, trackId);
                break;
            case "previous":
                _ = SendPlaybackAsync(PlaybackAction.Previous, trackId);
                break;
            case "next":
                _ = SendPlaybackAsync(PlaybackAction.Next, trackId);
                break;
            case "side":
                HandleSide(root, trackId);
                break;
            case "lyricsWindow":
                _showLyricsWindow();
                break;
            case "banner":
                if (_store.Snapshot.Track?.Id == trackId && trackId is not null)
                    _showBanner();
                break;
            case "settings":
                _showSettings();
                break;
            case "quit":
                _exit();
                break;
            case "offset":
                HandleOffset(root, trackId);
                break;
            case "position":
                HandlePosition(root);
                break;
        }
    }

    private void HandleSide(JsonElement root, string? trackId)
    {
        var side = root.TryGetProperty("side", out var sideElement) ? sideElement.GetString() : null;
        var action = side switch
        {
            "left" => _settings.LeftIconAction,
            "right" => _settings.RightIconAction,
            _ => IslandIconAction.None
        };
        var snapshot = _store.Snapshot;
        var requiresTrack = action is not (IslandIconAction.LyricsWindow or IslandIconAction.TemporaryHide);
        if (requiresTrack && (snapshot.Track is null || snapshot.Track.Id != trackId))
            return;
        switch (action)
        {
            case IslandIconAction.Favorite:
                _ = SendPlaybackAsync(PlaybackAction.Favorite, trackId,
                    snapshot.Controls.IsFavorite == true ? 0 : 1);
                break;
            case IslandIconAction.LyricsWindow:
                _showLyricsWindow();
                break;
            case IslandIconAction.PlayPause:
                _ = SendPlaybackAsync(PlaybackAction.PlayPause, trackId);
                break;
            case IslandIconAction.Previous:
                _ = SendPlaybackAsync(PlaybackAction.Previous, trackId);
                break;
            case IslandIconAction.Next:
                _ = SendPlaybackAsync(PlaybackAction.Next, trackId);
                break;
            case IslandIconAction.Banner:
                if (snapshot.Track is not null)
                    _showBanner();
                break;
            case IslandIconAction.CopyLink:
                if (snapshot.Track is { ShareUrl: not null } track)
                    _ = CopyTrackLinkAsync(track);
                break;
            case IslandIconAction.TemporaryHide:
                Send(new { type = "hide", seconds = SettingsStore.NormalizeTemporaryHideSeconds(
                    _settings.TemporaryHideSeconds) });
                break;
        }
    }

    private static async Task CopyTrackLinkAsync(TrackInfo track)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                { MainWindow.Clipboard: { } clipboard })
            {
                var artists = track.Artists.IsDefaultOrEmpty ? "未知艺术家" : string.Join(" / ", track.Artists);
                await clipboard.SetTextAsync($"{track.Title} - {artists}\n{track.ShareUrl}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[macos-glass] 复制歌曲链接失败：{exception.Message}");
        }
    }

    private void HandleOffset(JsonElement root, string? trackId)
    {
        if (!_settings.EnableTrackOffsets
            || _store.Snapshot.Track is not { } track
            || track.Id != trackId
            || !root.TryGetProperty("delta", out var deltaElement))
            return;
        var current = _settings.TrackOffsetFor(track.Id);
        var updated = (int)Math.Clamp((long)current + deltaElement.GetInt32(),
            SettingsStore.MinimumLyricsOffsetMs, SettingsStore.MaximumLyricsOffsetMs);
        if (updated != current)
            _trackOffsetChanged(track, updated);
    }

    private void HandlePosition(JsonElement root)
    {
        if (!_settings.RememberPosition)
            return;
        try
        {
            var x = root.GetProperty("x").GetDouble();
            var y = root.GetProperty("y").GetDouble();
            var display = root.TryGetProperty("displayId", out var displayElement)
                ? displayElement.GetString() : null;
            if (!double.IsFinite(x) || !double.IsFinite(y)
                || x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue)
                return;
            _applySettings(_settings with
            {
                WindowX = (int)Math.Round(x),
                WindowY = (int)Math.Round(y),
                DisplayId = string.IsNullOrWhiteSpace(display) ? null : display
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"[macos-glass] invalid window position: {exception.Message}");
        }
    }

    private async Task SendPlaybackAsync(PlaybackAction action, string? trackId, double value = 0)
    {
        if (_disposed)
            return;
        var snapshot = _store.Snapshot;
        if (_busy || snapshot.Track is null || snapshot.Track.Id != trackId)
        {
            SendControlFailed(action, trackId);
            return;
        }
        var allowed = action switch
        {
            PlaybackAction.PlayPause => snapshot.Controls.CanPlayPause,
            PlaybackAction.Previous => snapshot.Controls.CanPrevious,
            PlaybackAction.Next => snapshot.Controls.CanNext,
            PlaybackAction.Favorite => snapshot.Controls.CanFavorite,
            _ => false
        };
        if (!allowed)
        {
            SendControlFailed(action, trackId);
            return;
        }
        _busy = true;
        SendPlayback();
        try
        {
            var error = await _controlPlayback(
                new PlaybackCommand(action, value, trackId), _lifetime.Token);
            if (error is not null)
            {
                Console.Error.WriteLine($"[macos-glass] {error}");
                SendControlFailed(action, trackId);
            }
        }
        catch (OperationCanceledException)
        {
            SendControlFailed(action, trackId);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[macos-glass] playback control failed: {exception.Message}");
            SendControlFailed(action, trackId);
        }
        finally
        {
            _busy = false;
            SendPlayback();
        }
    }

    private void SendControlFailed(PlaybackAction action, string? trackId)
    {
        if (action == PlaybackAction.PlayPause)
            Send(new { type = "controlFailed", trackId });
    }

    private void SendSettings()
    {
        Send(new
        {
            type = "settings",
            settings = new
            {
                showIsland = _settings.ShowIsland,
                showTranslation = _settings.ShowTranslation,
                scale = _settings.ScalePercent / 100d,
                maxWidthPercent = _settings.WidthPercent,
                backgroundOpacityPercent = _settings.BackgroundOpacityPercent,
                rememberPosition = _settings.RememberPosition,
                lockPosition = _settings.LockPosition,
                clickThrough = _settings.ClickThrough,
                pausedDisplay = _settings.PausedDisplay.ToString(),
                temporaryHideSeconds = _settings.TemporaryHideSeconds,
                leftIconAction = _settings.LeftIconAction.ToString(),
                rightIconAction = _settings.RightIconAction.ToString(),
                maxFrameRate = _settings.MaximumFrameRate,
                lyricsLift = _settings.IslandLyricsLift,
                lyricsLiftMode = _settings.IslandLyricsLiftMode.ToString(),
                glowIntensity = _settings.IslandGlowIntensity,
                topOffset = _settings.TopOffset,
                windowX = _settings.WindowX,
                windowY = _settings.WindowY,
                displayId = _settings.DisplayId,
                enableTrackOffsets = _settings.EnableTrackOffsets,
                trackOffsetMs = _settings.TrackOffsetFor(_sentTrack?.Id)
            }
        });
    }

    private void SendApplicationIcon()
    {
        if (_sentApplicationIcon)
            return;
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Kashishu.icon.png");
        if (stream is null)
            return;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Send(new { type = "appIcon", appIcon = Convert.ToBase64String(memory.ToArray()) });
        _sentApplicationIcon = true;
    }

    private void SendTrack(TrackInfo? track, bool force = false)
    {
        if (!force && ReferenceEquals(track, _sentTrack))
            return;
        var trackChanged = !ReferenceEquals(track, _sentTrack);
        _sentTrack = track;
        if (track is null)
        {
            Send(new { type = "track" });
            return;
        }
        Send(new
        {
            type = "track",
            track = new
            {
                id = track.Id,
                title = track.Title,
                artists = track.Artists.IsDefaultOrEmpty ? Array.Empty<string>() : track.Artists.ToArray(),
                album = track.Album,
                durationMs = track.DurationMs,
                albumArt = Base64(track.AlbumArtBytes),
                artistImages = track.ArtistImageBytes.IsDefaultOrEmpty
                    ? Array.Empty<string>()
                    : track.ArtistImageBytes.Select(Base64).ToArray(),
                lyrics = track.Lyrics.IsDefaultOrEmpty
                    ? []
                    : track.Lyrics.Select(line => new
                    {
                        text = line.Text,
                        translation = line.Translation,
                        isAiTranslation = line.IsAiTranslation,
                        startMs = line.StartMs,
                        endMs = line.EndMs,
                        syllables = line.Syllables.IsDefaultOrEmpty
                            ? []
                            : LyricsTextLayout.EffectiveSyllables(line, _settings.EnglishWordEffects).Select(syllable => new
                            {
                                text = syllable.Text,
                                startMs = syllable.StartMs,
                                endMs = syllable.EndMs
                            }).ToArray()
                    }).ToArray()
            }
        });
        if (trackChanged)
            SendSettings();
    }

    private static string? Base64(System.Collections.Immutable.ImmutableArray<byte> bytes) =>
        bytes.IsDefaultOrEmpty ? null : Convert.ToBase64String(bytes.AsSpan());

    private void SendPlayback()
    {
        if (!_running)
            return;
        var snapshot = _store.Snapshot;
        var track = snapshot.Track;
        SendTrack(track);
        var trackOffset = _settings.EnableTrackOffsets && track is not null
            ? _settings.TrackOffsetFor(track.Id) : 0;
        var position = snapshot.PositionMs + _settings.LyricsOffsetMs + trackOffset;
        if (track is { DurationMs: > 0 })
            position = Math.Clamp(position, 0, track.DurationMs);
        else
            position = Math.Max(0, position);
        Send(new
        {
            type = "playback",
            playback = new
            {
                trackId = track?.Id,
                positionMs = position,
                isPlaying = snapshot.IsPlaying,
                status = snapshot.Status ?? string.Empty,
                attention = PlaybackStore.RequiresAttention(snapshot.Status ?? string.Empty),
                canPlayPause = snapshot.Controls.CanPlayPause,
                canPrevious = snapshot.Controls.CanPrevious,
                canNext = snapshot.Controls.CanNext,
                canFavorite = snapshot.Controls.CanFavorite,
                isFavorite = snapshot.Controls.IsFavorite == true,
                busy = _busy,
                canCopyLink = track?.ShareUrl is not null
            }
        });
    }

    private void Send(object message)
    {
        if (!_running)
            return;
        var json = JsonSerializer.Serialize(message, JsonOptions);
        if (_messages?.Writer.TryWrite(json) != true)
            PostFailure(_process, "helper is not consuming messages");
    }

    private async Task WriteMessagesAsync(
        Process process, StreamWriter input, Channel<string> messages, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in messages.Reader.ReadAllAsync(cancellationToken))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await input.WriteLineAsync(message.AsMemory(), timeout.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested)
                PostFailure(process, $"write failed: {exception.Message}");
        }
        finally
        {
            try { input.Dispose(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
    }

    private void PostFailure(Process? process, string reason) => Dispatcher.UIThread.Post(() =>
    {
        if (_stopping || _disposed || _failurePosted || process != _process)
            return;
        _failurePosted = true;
        Console.Error.WriteLine($"[macos-glass] {reason}; restoring the Avalonia island.");
        StopProcess();
        _failed();
    });

    private void StopProcess()
    {
        _stopping = true;
        _running = false;
        _store.Changed -= OnStoreChanged;
        _playbackTimer?.Stop();
        _playbackTimer = null;
        _messages?.Writer.TryWrite(JsonSerializer.Serialize(new { type = "quit" }, JsonOptions));
        _messages?.Writer.TryComplete();
        _messages = null;
        if (_process is { } process)
        {
            process.Exited -= OnExited;
            try
            {
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(800))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"[macos-glass] stop failed: {exception.Message}");
            }
            finally
            {
                _writerCancellation?.Cancel();
                process.Dispose();
                _process = null;
            }
        }
        _writerCancellation?.Dispose();
        _writerCancellation = null;
        _sentTrack = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopProcess();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
