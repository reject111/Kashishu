namespace LyricifyIsland;

/// <summary>Per-OS defaults shared by rendering, storage and the settings UI.</summary>
internal static class PlatformInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Simplified-Chinese UI and lyric font.</summary>
    public static string CjkFontFamily => IsWindows ? "Microsoft YaHei UI" : "Noto Sans CJK SC";

    /// <summary>Japanese lyric font (kana/kanji runs). The UI variant of Yu Gothic is drawn
    /// tighter than the Chinese lyric font, so the regular family is preferred when available.</summary>
    public static string JapaneseFontFamily => IsWindows ? "Yu Gothic" : "Noto Sans CJK JP";

    /// <summary>Latin lyric font. Montserrat ships with many Linux desktops; Windows uses Segoe UI.</summary>
    public static string LatinFontFamily => IsWindows ? "Segoe UI" : "Montserrat";

    // Fallbacks are tried in order; see LyricsFonts.ResolveTypeface.
    public static string[] CjkFontCandidates => [CjkFontFamily];
    public static string[] JapaneseFontCandidates => IsWindows
        ? ["Yu Gothic", "Yu Gothic UI", "Meiryo UI", "MS Gothic"]
        : [JapaneseFontFamily];
    public static string[] LatinFontCandidates => [LatinFontFamily];

    /// <summary>Name of the local (non-Spotify) playback source shown in the UI.</summary>
    public static string LocalPlaybackSourceName => IsWindows ? "本地媒体会话" : "本地 MPRIS";

    public static string LocalPlaybackConnectingStatus =>
        IsWindows ? "正在连接本地媒体会话…" : "正在连接 MPRIS…";

    public static string NoActiveLocalPlaybackStatus =>
        IsWindows ? PlaybackStore.NoActiveWindowsPlaybackStatus : PlaybackStore.NoActiveMprisPlaybackStatus;
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
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    public static string StateHome()
    {
        if (Rooted(Environment.GetEnvironmentVariable("XDG_STATE_HOME")) is { } directory)
            return directory;
        if (PlatformInfo.IsWindows)
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
    }

    public static string CacheHome()
    {
        if (Rooted(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")) is { } directory)
            return directory;
        if (PlatformInfo.IsWindows)
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    }

    private static string? Rooted(string? value) =>
        string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) ? null : value;
}
