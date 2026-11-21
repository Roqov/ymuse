using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Ymuse;

/// <summary>
/// Вход через Яндекс: окно с настоящей страницей oauth.yandex.ru.
/// После подтверждения Яндекс редиректит на music.yandex.ru с токеном во фрагменте URL,
/// токен перехватывается и окно закрывается само.
/// </summary>
public partial class LoginWindow : Window
{
    const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";

    static readonly string AuthorizeUrl =
        $"https://oauth.yandex.ru/authorize?response_type=token&client_id={ClientId}";

    readonly TaskCompletionSource<string?> _result = new();

    public static Task<string?> SignInAsync(Window owner)
    {
        var window = new LoginWindow { Owner = owner };
        window.Show();
        return window._result.Task;
    }

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _result.TrySetResult(null);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Тот же отладочный хук, что и в главном окне, но со своим портом и профилем:
            // две среды WebView2 не могут делить одну папку данных.
            var cdpPort = Environment.GetEnvironmentVariable("YMUSE_CDP_PORT");
            if (int.TryParse(cdpPort, out var port) && port > 0)
            {
                var profile = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ymuse", "login-webview");
                var options = new CoreWebView2EnvironmentOptions($"--remote-debugging-port={port + 1}");
                var env = await CoreWebView2Environment.CreateAsync(null, profile, options);
                await Browser.EnsureCoreWebView2Async(env);
            }
            else
            {
                await Browser.EnsureCoreWebView2Async();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть окно входа: " + ex.Message, "Ymuse",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, args) => Finish(args.Uri, args);
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess) Finish(core.Source, null);
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(core.DocumentTitle)) Title = core.DocumentTitle + " — Ymuse";
        };

        Browser.Source = new Uri(AuthorizeUrl);
    }

    void Finish(string? url, CoreWebView2NavigationStartingEventArgs? navigation)
    {
        var token = ExtractToken(url);
        if (string.IsNullOrEmpty(token)) return;

        if (navigation != null) navigation.Cancel = true; // дальше music.yandex.ru не грузим: токен уже есть
        if (_result.TrySetResult(token)) Close();
    }

    static string? ExtractToken(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var match = Regex.Match(url, @"[#?&]access_token=([^&]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }
}
