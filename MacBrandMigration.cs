using System.Diagnostics;

namespace Kashishu;

/// <summary>macOS rename migration, run before any stores open their files.</summary>
internal static class MacBrandMigration
{
    internal static string LaunchAgentsDirectory =>
        Environment.GetEnvironmentVariable("KASHISHU_LAUNCH_AGENTS_HOME") is { } path && Path.IsPathRooted(path)
            ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents");

    internal static void Run(bool migrateAutostart)
    {
        foreach (var home in new[] { AppPaths.ConfigHome(), AppPaths.StateHome(), AppPaths.CacheHome() }
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                var legacy = Path.Combine(home, "lyricify-island");
                // Never move a symlink to a directory outside the application's data roots.
                if (Directory.Exists(legacy) && (File.GetAttributes(legacy) & FileAttributes.ReparsePoint) == 0)
                    AppPaths.MigrateBrandDirectory(home);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[macos-migration] could not inspect legacy data: {exception.Message}");
            }
        }
        if (migrateAutostart)
            MigrateLaunchAgent(LaunchAgentsDirectory, SettingsStore.Load().StartOnLogin, Environment.ProcessPath,
                unloadLegacy: true);
    }

    internal static void MigrateLaunchAgent(string directory, bool enabled, string? executable,
        bool unloadLegacy = false)
    {
        var legacy = Path.Combine(directory, "com.lyricifyisland.app.plist");
        if (!File.Exists(legacy)) return;
        var target = Path.Combine(directory, "com.kashishu.app.plist");
        try
        {
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(executable)) return;
                var temporary = Path.Combine(directory, $".kashishu.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temporary, Autostart.LaunchAgent(executable));
                    File.Move(temporary, target, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else
                File.Delete(target);

            if (unloadLegacy && OperatingSystem.IsMacOS())
            {
                // bootout also succeeds for a disabled job; an unregistered old job is harmless.
                using var process = Process.Start(new ProcessStartInfo("/bin/launchctl")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    ArgumentList = { "bootout", $"gui/{GetUserId()}/com.lyricifyisland.app" }
                });
                if (process is not null && !process.WaitForExit(2000)) process.Kill();
            }
            File.Delete(legacy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[macos-migration] could not update login item: {exception.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("libSystem.B.dylib", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}
