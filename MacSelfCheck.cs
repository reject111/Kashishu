namespace Kashishu;

internal static class MacSelfCheck
{
    internal static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        var root = Path.Combine(Path.GetTempPath(), $"kashishu-mac-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var overrides = new[] { "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME" };
        var previous = overrides.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            for (var index = 0; index < overrides.Length; index++)
            {
                var home = Path.Combine(root, $"data-{index}");
                Environment.SetEnvironmentVariable(overrides[index], home);
                var oldData = Path.Combine(home, "lyricify-island");
                var newData = Path.Combine(home, "kashishu");
                Directory.CreateDirectory(oldData);
                Directory.CreateDirectory(newData);
                File.WriteAllText(Path.Combine(oldData, "recover.json"), "legacy token or cache");
                File.WriteAllText(Path.Combine(oldData, "settings.json"), "old settings");
                File.WriteAllText(Path.Combine(newData, "settings.json"), "new settings");
            }
            MacBrandMigration.Run(migrateAutostart: false);
            MacBrandMigration.Run(migrateAutostart: false);
            foreach (var variable in overrides)
            {
                var home = Environment.GetEnvironmentVariable(variable)!;
                Require(File.ReadAllText(Path.Combine(home, "kashishu", "recover.json")) == "legacy token or cache"
                    && File.ReadAllText(Path.Combine(home, "kashishu", "settings.json")) == "new settings"
                    && File.ReadAllText(Path.Combine(home, "lyricify-island", "settings.json")) == "old settings",
                    "macOS migration recovers missing data without overwriting either version's settings");
            }
            var legacy = Path.Combine(root, "com.lyricifyisland.app.plist");
            var target = Path.Combine(root, "com.kashishu.app.plist");
            File.WriteAllText(legacy, "old job");
            const string executable = "/Applications/Kashishu & Music.app/Contents/MacOS/Kashishu";
            MacBrandMigration.MigrateLaunchAgent(root, true, executable);
            var plist = File.ReadAllText(target);
            Require(!File.Exists(legacy) && plist.Contains("com.kashishu.app")
                && plist.Contains("Kashishu &amp; Music.app") && !plist.Contains("lyricifyisland"),
                "macOS enabled login item migrates with escaped path and new identity");
            MacBrandMigration.MigrateLaunchAgent(root, true, executable);
            Require(File.ReadAllText(target) == plist, "macOS migration is idempotent");
            File.WriteAllText(legacy, "old job");
            MacBrandMigration.MigrateLaunchAgent(root, false, executable);
            Require(!File.Exists(legacy) && !File.Exists(target), "disabled macOS login items stay disabled");

            using var players = System.Text.Json.JsonDocument.Parse("""
                {"source":"spotify","title":"test","state":"playing","trackId":"spotify:track:4uLU6hMCjMI75M1A2tKUQC"}
                """);
            var player = MacMediaService.ParsePlayer(players.RootElement)!;
            Require(MacMediaService.ShareUrl(player) == "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC"
                && MacMediaService.ShareUrl(player with { TrackId = "spotify:local:file" }) is null
                && MacMediaService.ShareUrl(player with { Source = "music" }) is null,
                "macOS sharing uses public track URLs, never local library identities");
        }
        finally
        {
            for (var index = 0; index < overrides.Length; index++)
                Environment.SetEnvironmentVariable(overrides[index], previous[index]);
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("macOS migration and sharing checks: ok");
    }
}
