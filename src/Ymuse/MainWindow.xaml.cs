using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Ymuse.Api;
using Ymuse.Bridge;
using Ymuse.Core;

namespace Ymuse;

public partial class MainWindow : Window
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    readonly SettingsStore _store = new();
    readonly YandexClient _api = new();
    readonly UpdateService _updates;
    readonly Rpc _rpc;

    public MainWindow()
    {
        InitializeComponent();
        _store.Load();
        _api.Token = _store.Data.Token;
        _updates = new UpdateService(_store);
        _updates.NormalizeState();
        _rpc = new Rpc(_api, _store, _updates, BrowserSignInAsync);
        Loaded += OnLoaded;
        Closed += (_, _) => _updates.ApplyOnExit();
    }

    /// <summary>Открывает окно Яндекс-входа и возвращает OAuth-токен (null — пользователь закрыл окно).</summary>
    async Task<string?> BrowserSignInAsync()
    {
        var operation = Dispatcher.InvokeAsync(() => LoginWindow.SignInAsync(this));
        return await operation.Task.Unwrap();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Отладочный хук: YMUSE_CDP_PORT=9333 открывает DevTools-порт WebView2.
            // Нужен только для автоскриншотов и диагностики, обычный запуск его не использует.
            var cdpPort = Environment.GetEnvironmentVariable("YMUSE_CDP_PORT");
            if (int.TryParse(cdpPort, out var port) && port > 0)
            {
                var options = new CoreWebView2EnvironmentOptions($"--remote-debugging-port={port}");
                var env = await CoreWebView2Environment.CreateAsync(null, null, options);
                await Browser.EnsureCoreWebView2Async(env);
            }
            else
            {
                await Browser.EnsureCoreWebView2Async();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось запустить компонент WebView2 (Edge).\n" +
                "Установите «Microsoft Edge WebView2 Runtime» и запустите Ymuse снова.\n\n" + ex.Message,
                "Ymuse", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping("app.ymuse", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                MessageBox.Show("Не удалось загрузить интерфейс приложения.", "Ymuse",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        Browser.Source = new Uri("https://app.ymuse/index.html");

        // тихая проверка обновлений: раз в 6 часов, без диалогов и без остановки воспроизведения
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await _updates.CheckAsync();
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        double id = 0;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl))
            {
                id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetDouble() : 0;
            }
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            var pars = root.TryGetProperty("params", out var p) ? p.Clone() : default;

            var data = await _rpc.HandleAsync(method, pars);
            Send(new { id, ok = true, data });
        }
        catch (Exception ex)
        {
            Send(new { id, ok = false, error = ex.Message });
        }
    }

    void Send(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        Dispatcher.Invoke(() => Browser.CoreWebView2?.PostWebMessageAsJson(json));
    }
}
