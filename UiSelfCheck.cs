using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Kashishu;

// Runs inside a real desktop lifetime: renderer-only tests cannot catch duplicate parents,
// deferred layout failures, or a cached settings page overwriting changes from the header.
internal static class UiSelfCheck
{
    public static async Task RunAsync(LyricsWindow window, Func<IslandSettings> current,
        Func<IslandSettings, bool> apply)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Task.Delay(500);
        var page = window.GetLogicalDescendants().OfType<SettingsPage>().Single();
        T Named<T>(string name) where T : Control => page.GetLogicalDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name);

        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "Spotify Client ID", "Spotify Client Secret", "AI 翻译 API Key",
                         "AI 翻译 Base URL", "AI 翻译模型" })
                CheckTextCenter(Named<TextBox>(name));
            var language = Named<ComboBox>("AI 翻译目标语言");
            CheckTextCenter(language.GetVisualDescendants().OfType<TextBox>().Single());
            Require(page.GetLogicalDescendants().OfType<TextBlock>()
                .Where(text => text.Text is "本地缓存" or "翻译缓存").All(text => text.FontWeight == FontWeight.Bold),
                "settings preserve Linux's title emphasis using the installed Windows bold face");
            Require(window.WindowDecorations == WindowDecorations.Full,
                "native window styles remain available for DWM transitions");
            Require(window.ExtendClientAreaToDecorationsHint && window.ExtendClientAreaTitleBarHeightHint == 62,
                "platform caption controls share the immersive header");
        }

        if (OperatingSystem.IsMacOS())
        {
            var language = Named<ComboBox>("AI 翻译目标语言");
            CheckTextCenter(language.GetVisualDescendants().OfType<TextBox>().Single());
            Require(Application.Current?.Name == "Kashishu", "macOS application menu uses the product name");
        }

        var translation = Named<ToggleSwitch>("显示歌词翻译");
        var original = current();
        Require(apply(original with { ShowTranslation = !original.ShowTranslation }), "external settings save");
        Require(translation.IsChecked == !original.ShowTranslation, "header translation reaches settings");
        var lift = Named<ToggleSwitch>("灵动岛歌词上下运动");
        lift.IsChecked = !original.IslandLyricsLift;
        Require(current().IslandLyricsLift != original.IslandLyricsLift, "lyric lift saves");
        Require(current().ShowTranslation != original.ShowTranslation, "unrelated setting preserves header translation");
        translation.IsChecked = original.ShowTranslation;
        Require(current().ShowTranslation == original.ShowTranslation, "translation toggle saves");
        Require(apply(original), "restore settings");

        // Exercise all eight navigation targets and let their scrolling/layout finish.
        var navigation = page.GetLogicalDescendants().OfType<Button>()
            .Where(button => (AutomationProperties.GetName(button) ?? "").StartsWith("跳转到"))
            .ToArray();
        Require(navigation.Length == 8, "all settings sections exist");
        foreach (var button in navigation)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(400);
            Require(page.Bounds.Width > 0 && page.Bounds.Height > 0, "settings section layout");
        }

        var ai = Named<ToggleSwitch>("启用 AI 歌词翻译");
        if (string.IsNullOrWhiteSpace(current().AiTranslationApiKey))
            Require(!ai.IsEnabled, "AI translation requires a key");
        using (var server = new TranslationTestServer())
        {
            var key = Named<TextBox>("AI 翻译 API Key");
            var url = Named<TextBox>("AI 翻译 Base URL");
            var model = Named<TextBox>("AI 翻译模型");
            key.Text = "local-test-key";
            url.Text = server.BaseUrl;
            model.Text = "test-model";
            await Task.Delay(100); // TextChanged is dispatched after the Text properties change.
            ai.IsChecked = true;
            await Eventually(() => current().AiTranslationEnabled && ai.IsEnabled);
            Require(ai.IsChecked == true && server.Requests == 1, "AI enables after successful connection test");
            ai.IsChecked = false;
            server.Status = 401;
            ai.IsChecked = true;
            await Eventually(() => server.Requests == 2 && ai.IsEnabled);
            Require(!current().AiTranslationEnabled && ai.IsChecked != true,
                "failed AI authentication leaves translation disabled");
            url.Text = "invalid-url";
            await Task.Delay(100);
            ai.IsChecked = true;
            await Eventually(() => ai.IsEnabled && ai.IsChecked != true);
            Require(server.Requests == 2 && !current().AiTranslationEnabled,
                "invalid AI endpoint is rejected before sending a request");
            Require(window.GetLogicalDescendants().OfType<TextBlock>()
                .Any(text => text.Text == "测试失败：请检查翻译设置"), "AI failure toast visible over settings");
            key.Text = original.AiTranslationApiKey;
            url.Text = original.AiTranslationBaseUrl;
            model.Text = original.AiTranslationModel;
            Require(apply(original), "restore translation settings");
        }
        foreach (var kind in new[] { PanelKind.Banner, PanelKind.Settings, PanelKind.Banner, PanelKind.Settings })
        {
            window.OpenPanel(kind);
            await Task.Delay(450);
        }
        Require(window.GetLogicalDescendants().OfType<SettingsPage>().Single() == page,
            "settings page survives repeated panel transitions");
        window.WindowState = WindowState.Maximized;
        await Task.Delay(200);
        window.WindowState = WindowState.Normal;
        window.WindowState = WindowState.Minimized;
        await Task.Delay(250);
        window.RestoreFromMinimized();
        await Task.Delay(300);
        Require(window.WindowState == WindowState.Normal, "restore after minimizing");
        window.WindowState = WindowState.FullScreen;
        await Task.Delay(350);
        Require(window.Content is Control { Opacity: 1 }, "fullscreen transition settles");
        window.WindowState = WindowState.Normal;
        await Task.Delay(350);
        window.Width = 640;
        window.Height = 540;
        await Task.Delay(300);
        Require(page.Bounds.Width > 0, "compact settings layout");
        window.Close();
    }

    private static void CheckTextCenter(TextBox input)
    {
        var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
        var origin = presenter.TranslatePoint(default, input)
            ?? throw new InvalidOperationException("Text presenter is detached");
        var difference = Math.Abs(origin.Y + presenter.Bounds.Height / 2 - input.Bounds.Height / 2);
        if (difference > 1)
            throw new InvalidOperationException($"Text is not vertically centered: {AutomationProperties.GetName(input)} ({difference:0.##} px)");
    }

    private static async Task Eventually(Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var timeout = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException($"UI state did not settle: {description}");
            await Task.Delay(25);
        }
    }

    // An actual OpenAI-compatible HTTP exchange, confined to loopback with dummy credentials.
    private sealed class TranslationTestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Requests;
        public int Status = 200;
        public string BaseUrl { get; }

        public TranslationTestServer()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(header[15..].Trim());
                    var body = new char[length];
                    await reader.ReadBlockAsync(body.AsMemory(), _stop.Token);
                    var status = Status;
                    var content = status == 200
                        ? JsonSerializer.Serialize(new
                        {
                            id = "chatcmpl-test", @object = "chat.completion", created = 0, model = "test-model",
                            choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } }
                        })
                        : "{\"error\":{\"message\":\"Invalid test key\",\"type\":\"invalid_api_key\"}}";
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                    Interlocked.Increment(ref Requests);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
