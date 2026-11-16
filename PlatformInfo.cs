namespace Kashishu;

/// <summary>Per-OS defaults shared by rendering, storage and the settings UI.</summary>
internal static class PlatformInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>Windows and macOS let the app position a borderless window itself; Wayland
    /// compositors place toplevels for the client.</summary>
    public static bool UsesExplicitWindowPlacement => IsWindows || IsMacOS;

    /// <summary>Simplified-Chinese UI and lyric font.</summary>
    public static string CjkFontFamily => IsWindows ? "Microsoft YaHei UI" : IsMacOS ? "PingFang SC" : "Noto Sans CJK SC";

    /// <summary>Japanese lyric font (kana/kanji runs). The UI variant of Yu Gothic is drawn
    /// tighter than the Chinese lyric font, so the regular family is preferred when available.</summary>
    public static string JapaneseFontFamily => IsWindows ? "Yu Gothic" : IsMacOS ? "Hiragino Sans" : "Noto Sans CJK JP";

    /// <summary>Latin lyric font. Montserrat ships with many Linux desktops; Windows uses Segoe UI.</summary>
    public static string LatinFontFamily => IsWindows ? "Segoe UI" : IsMacOS ? "Helvetica Neue" : "Montserrat";

    // Fallbacks are tried in order; see LyricsFonts.ResolveTypeface.
    public static string[] CjkFontCandidates => IsMacOS
        ? ["PingFang SC", "Hiragino Sans GB", "Heiti SC"]
        : [CjkFontFamily];
    public static string[] JapaneseFontCandidates => IsWindows
        ? ["Yu Gothic", "Yu Gothic UI", "Meiryo UI", "MS Gothic"]
        : IsMacOS
            ? ["Hiragino Sans", "Hiragino Kaku Gothic ProN", "Yu Gothic"]
            : [JapaneseFontFamily];
    public static string[] LatinFontCandidates => IsMacOS
        ? ["Helvetica Neue", "Helvetica", "Arial"]
        : [LatinFontFamily];

    /// <summary>Name of the local (non-Spotify) playback source shown in the UI.</summary>
    public static string LocalPlaybackSourceName =>
        IsWindows ? "本地媒体会话" : IsMacOS ? "本地播放器" : "本地 MPRIS";

    public static string LocalPlaybackConnectingStatus =>
        IsWindows ? "正在连接本地媒体会话…" : IsMacOS ? "正在连接本地播放器…" : "正在连接 MPRIS…";

    public static string NoActiveLocalPlaybackStatus =>
        IsWindows ? PlaybackStore.NoActiveWindowsPlaybackStatus
        : IsMacOS ? PlaybackStore.NoActiveMacPlaybackStatus
        : PlaybackStore.NoActiveMprisPlaybackStatus;
}

/// <summary>Config, state and cache locations. Environment overrides are honoured on every
/// platform so the self-test can redirect them to a temporary directory.</summary>
internal static class AppPaths
{
    public static string ConfigHome()
    {
        if (Rooted(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) is { } directory)
            return directory;
        if (PlatformInfo.IsWindows)
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (PlatformInfo.IsMacOS)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    public static string StateHome()
    {
        if (Rooted(Environment.GetEnvironmentVariable("XDG_STATE_HOME")) is { } directory)
            return directory;
        if (PlatformInfo.IsWindows)
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (PlatformInfo.IsMacOS)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
    }

    public static string CacheHome()
    {
        if (Rooted(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")) is { } directory)
            return directory;
        if (PlatformInfo.IsWindows)
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (PlatformInfo.IsMacOS)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    }

    /// <summary>The upstream rename moves Windows data from lyricify-island to kashishu.
    /// Move the old per-user directories once so settings and Spotify authorization survive.</summary>
    public static void MigrateLegacyWindowsBrandData()
    {
        if (!PlatformInfo.IsWindows)
            return;
        MigrateBrandDirectory(ConfigHome());
        MigrateBrandDirectory(StateHome());
        MigrateBrandDirectory(CacheHome());
    }

    internal static void MigrateBrandDirectory(string home)
    {
        var source = Path.Combine(home, "lyricify-island");
        var destination = Path.Combine(home, "kashishu");
        if (!Directory.Exists(source))
            return;
        try
        {
            Directory.CreateDirectory(home);
            if (!Directory.Exists(destination))
                Directory.Move(source, destination);
            else
                MergeLegacyDirectory(source, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[migration] could not move {source}: {exception.Message}");
        }
    }

    private static void MergeLegacyDirectory(string source, string destination)
    {
        // A previous launch can create the new settings directory before tokens or caches
        // are migrated. Keep newer files and recover missing files; never follow links.
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0
            || (Directory.Exists(destination)
                && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0))
            return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                File.Move(file, target);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            MergeLegacyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        if (!Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source);
    }

    private static string? Rooted(string? value) =>
        string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) ? null : value;
}
