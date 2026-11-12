using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Smena.Client;

/// <summary>
/// Единственное окно программы: WebView2 с интерфейсом кассы, который раздаёт сервер.
/// </summary>
internal sealed class MainForm : Form
{
    private const string RetryMessage = "retry";
    private const string SettingsMessage = "settings";

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private ConnectionSettings _settings;

    public MainForm(ConnectionSettings settings)
    {
        _settings = settings;

        Text = "Смена";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(1024, 640);
        BackColor = Color.FromArgb(0x16, 0x1d, 0x27);

        Controls.Add(_webView);
        Load += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Профиль браузера — в каталоге данных программы, а не рядом с exe:
            // каталог установки может быть недоступен для записи.
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(ConnectionSettings.DataDirectory, "WebView2"));
            await _webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(
                this,
                "Не удалось запустить WebView2. Установите Microsoft Edge WebView2 Runtime и запустите программу снова.\n\n" + e.Message,
                "Смена",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _webView.CoreWebView2;

        // Это касса, а не браузер: без контекстного меню, инструментов разработчика
        // и горячих клавиш браузера. F5 посреди заполнения отчёта сбросил бы форму.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += static (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;

        await InjectTokenAsync();
        core.Navigate(_settings.CashierUrl.ToString());
    }

    /// <summary>
    /// Передаёт интерфейсу токен устройства до загрузки страницы. Скрипт выполняется
    /// в каждом документе, поэтому сам проверяет адрес: чужая страница токен не получит.
    /// </summary>
    private async Task InjectTokenAsync()
    {
        var origin = JsonSerializer.Serialize(_settings.ServerUrl.GetLeftPart(UriPartial.Authority));
        var token = JsonSerializer.Serialize(_settings.Token);

        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            $"if (location.origin === {origin}) {{ window.__SMENA__ = {{ token: {token} }}; }}");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Окно показывает только сервер кассы и собственную страницу ошибки.
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            || !string.Equals(
                target.GetLeftPart(UriPartial.Authority),
                _settings.ServerUrl.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            _webView.CoreWebView2.NavigateToString(ErrorPage(_settings.ServerUrl, e.WebErrorStatus));
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Сообщения принимаются только от встроенной страницы ошибки (у неё нет адреса сервера).
        if (!e.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !e.Source.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (e.TryGetWebMessageAsString())
        {
            case RetryMessage:
                _webView.CoreWebView2.Navigate(_settings.CashierUrl.ToString());
                break;

            case SettingsMessage:
                using (var setup = new ConnectionSetupForm(_settings))
                {
                    if (setup.ShowDialog(this) == DialogResult.OK && setup.Result is { } updated)
                    {
                        // Токен вшит в скрипт, добавленный при запуске; проще и надёжнее
                        // перезапустить программу, чем подменять его на лету.
                        _settings = updated;
                        Application.Restart();
                        Close();
                    }
                }

                break;
        }

        await Task.CompletedTask;
    }

    private static string ErrorPage(Uri serverUrl, CoreWebView2WebErrorStatus status) => $$"""
        <!doctype html>
        <html lang="ru">
        <head>
          <meta charset="utf-8">
          <style>
            body { margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center;
                   background: #161d27; color: #e7ebf0; font-family: "Segoe UI", sans-serif; }
            .card { max-width: 460px; text-align: center; }
            h1 { font-size: 20px; font-weight: 600; color: #c0604a; margin: 0 0 10px; }
            p { color: #8b97a7; font-size: 14px; margin: 0 0 20px; }
            button { margin: 0 6px; padding: 9px 18px; border-radius: 6px; font-size: 14px; cursor: pointer; }
            .primary { background: #c08b3d; border: none; color: #191307; font-weight: 600; }
            .ghost { background: transparent; border: 1px solid #4a5a6e; color: #e7ebf0; }
          </style>
        </head>
        <body>
          <div class="card">
            <h1>Нет связи с сервером</h1>
            <p>{{System.Net.WebUtility.HtmlEncode(serverUrl.ToString())}} не отвечает ({{status}}).</p>
            <button class="primary" onclick="chrome.webview.postMessage('{{RetryMessage}}')">Повторить</button>
            <button class="ghost" onclick="chrome.webview.postMessage('{{SettingsMessage}}')">Настройки подключения</button>
          </div>
        </body>
        </html>
        """;
}
