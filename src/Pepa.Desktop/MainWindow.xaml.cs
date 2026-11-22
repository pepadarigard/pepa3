using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Pepa.Host;

namespace Pepa.Desktop;

/// <summary>
/// Окно Pepa: WebView2 с интерфейсом локального сервера и несколько нативных функций, которых нет в браузере
/// (выбор папки, открыть в проводнике). Нативные функции доступны только странице самого Pepa.
/// </summary>
public partial class MainWindow : Window
{
    private const string WebViewDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private readonly PepaHost _host;
    private readonly DesktopSettings _settings;
    private readonly bool _dark;
    private bool _shown;
    private bool _toldAboutTray;

    /// <summary>Окно можно закрыть: Pepa остановлена (или Windows завершает сеанс и остановку ведёт приложение).</summary>
    private bool _closeAllowed;

    /// <summary>Идёт вопрос перед закрытием (свернуть ли; закрыть ли при работающих процессах).</summary>
    private bool _asking;

    /// <summary>Открыт вопрос «Закрыть Pepa?» (о работающих процессах).</summary>
    private bool _askingExit;

    /// <summary>Выход подтверждён и идёт: остановка движка до закрытия окна.</summary>
    private Task? _exit;

    public MainWindow(PepaHost host, DesktopSettings settings)
    {
        _host = host;
        _settings = settings;
        _dark = IsSystemDark();
        InitializeComponent();
        ApplyTheme();
        Loaded += async (_, _) => await InitializeWebViewAsync();
        SourceInitialized += (_, _) => UseDarkTitleBar(_dark);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(_host.DataDirectory, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowProblem("Нужен компонент Microsoft Edge WebView2",
                "Окно Pepa показывает интерфейс через WebView2 Runtime — он встроен в Windows 11 и обычно установлен в Windows 10. " +
                "Установите его и перезапустите Pepa. Пока можно открыть интерфейс в браузере: движок уже работает.");
            return;
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось открыть интерфейс", $"{ex.Message}\n\nДвижок работает — интерфейс можно открыть в браузере.");
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        Web.DefaultBackgroundColor = _dark
            ? System.Drawing.Color.FromArgb(255, 14, 16, 21)
            : System.Drawing.Color.FromArgb(255, 245, 246, 248);
        core.Navigate(_host.LaunchUrl.ToString());
    }

    private bool IsOwnPage(string uri) => Pepa.Host.Security.OwnPage.Is(_host.BaseUrl, uri);

    /// <summary>Значок в области уведомлений (задаётся приложением).</summary>
    public TrayIcon? Tray { get; set; }

    /// <summary>
    /// «Выйти из Pepa» (меню значка): закрыть совсем, с прежним предупреждением о работающих процессах. Окно, запущенное
    /// свёрнутым, могло ни разу не показываться — выход не полагается на его закрытие.
    /// </summary>
    public async void ExitPepa() => await ExitAsync();

    /// <summary>Pepa закрывается (выход подтверждён): окно не открывается снова — остановка доводится до конца.</summary>
    public bool IsExiting => _exit is not null;

    /// <summary>
    /// Закрыть Pepa без вопросов (её закрывает программа обновления): обычная остановка с ожиданием выполняющихся шагов.
    /// Открытые вопросы о закрытии получают «Нет» — после них ничего не сохраняется и не останавливается: выход уже идёт.
    /// </summary>
    public void ExitWithoutQuestions()
    {
        _exit ??= StopAndShutdownAsync();
        if (_asking || _askingExit)
        {
            QuestionBoxes.AnswerNo();
        }
    }

    /// <summary>
    /// Pepa остановлена снаружи (завершение сеанса Windows): окно закрывается без вопросов. Само закрытие делает
    /// Application.Shutdown — Close() здесь не вызывается: окно может быть посреди закрытия (открыт вопрос).
    /// </summary>
    public void AllowClose() => _closeAllowed = true;

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Окно показывает только интерфейс Pepa; внешние ссылки — в обычном браузере.
        if (!IsOwnPage(e.Uri))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowProblem("Не удалось загрузить интерфейс", $"Ошибка WebView2: {e.WebErrorStatus}. Попробуйте перезапустить Pepa.");
            return;
        }

        if (!_shown)
        {
            _shown = true;
            Placeholder.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
        }
    }

    // ───────────── Нативные функции для интерфейса ─────────────

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsOwnPage(e.Source))
        {
            return;
        }

        int? id = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var value) ? value : null;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            object? result = type switch
            {
                "pickFolder" => PickFolder(root.TryGetProperty("initial", out var initial) ? initial.GetString() : null),
                "openFolder" => OpenFolder(root.GetProperty("path").GetString()),
                _ => throw new InvalidOperationException($"Неизвестная команда «{type}»."),
            };
            Reply(id, ok: true, result, null);
        }
        catch (Exception ex)
        {
            Reply(id, ok: false, null, ex.Message);
        }
    }

    private string? PickFolder(string? initial)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите рабочую папку Pepa",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initial) && !Pepa.Host.Security.OwnPage.IsNetworkPath(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static object? OpenFolder(string? path)
    {
        // Только существующие папки: так из интерфейса нельзя запустить произвольную программу. Сетевые пути — нет: даже
        // проверка такой папки отправила бы серверу учётные данные Windows.
        if (string.IsNullOrWhiteSpace(path) || Pepa.Host.Security.OwnPage.IsNetworkPath(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Папка не найдена.");
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetFullPath(path)}\"") { UseShellExecute = true });
        return null;
    }

    private void Reply(int? id, bool ok, object? result, string? error)
    {
        if (id is null || Web.CoreWebView2 is null)
        {
            return;
        }

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok, result, error }));
    }

    // ───────────── Закрытие ─────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeAllowed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_exit is not null)
        {
            // Pepa уже закрывается: окно снова скрыть, остановка идёт дальше.
            Hide();
            return;
        }

        // Вопросы — после выхода из закрытия окна: пока окно «закрывается», WPF не даёт вызвать Close(), и завершение сеанса
        // Windows в этот момент сломалось бы на ошибке.
        if (!_asking)
        {
            _asking = true;
            // async void (а не Task): ошибка дойдёт до общего обработчика ошибок окна, а не потеряется.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await OnCloseRequestedAsync();
                }
                finally
                {
                    _asking = false;
                }
            }));
        }
    }

    /// <summary>Крестик окна (а не «Выйти из Pepa»): по умолчанию — спросить один раз, свернуть ли в область уведомлений.</summary>
    private async Task OnCloseRequestedAsync()
    {
        if (_closeAllowed || _exit is not null)
        {
            return;
        }

        if (Tray is not null)
        {
            if (_settings.CloseAction == CloseAction.Ask)
            {
                var choice = MessageBox.Show(this,
                    "Свернуть Pepa в область уведомлений (рядом с часами)?\n\n" +
                    "Да — окно скроется, а процессы продолжат работать и ждать ответы; расписания будут запускаться. Открыть Pepa — " +
                    "двойной щелчок по значку.\nНет — закрыть Pepa (работающие процессы приостановятся).\n\n" +
                    "Выбор запомнится; изменить его можно в меню значка Pepa.",
                    "Pepa", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (choice == MessageBoxResult.Cancel || _closeAllowed || _exit is not null)
                {
                    return;
                }

                _settings.CloseAction = choice == MessageBoxResult.Yes ? CloseAction.Tray : CloseAction.Exit;
                _settings.Save();
                // Только эта отметка: «Запускать вместе с Windows» — как сейчас в меню (и в реестре).
                Tray.SetCloseToTray(_settings.CloseAction == CloseAction.Tray);
            }

            if (_settings.CloseAction == CloseAction.Tray)
            {
                Hide();
                if (!_toldAboutTray)
                {
                    _toldAboutTray = true;
                    Tray.ShowBalloon("Pepa работает", "Окно свёрнуто в область уведомлений — процессы продолжаются. Открыть — двойной щелчок по значку.");
                }

                return;
            }
        }

        await ExitAsync();
    }

    /// <summary>Завершить Pepa: предупредить о работающих процессах, дождаться движка и закрыть приложение.</summary>
    private async Task ExitAsync()
    {
        if (_exit is not null)
        {
            await _exit;
            return;
        }

        if (_askingExit)
        {
            // Вопрос «Закрыть Pepa?» уже открыт.
            return;
        }

        _askingExit = true;
        try
        {
            int active;
            try
            {
                active = await _host.CountActiveProcessesAsync();
            }
            catch
            {
                active = 0;
            }

            if (active > 0)
            {
                const string text = "При закрытии они будут приостановлены. После следующего запуска Pepa ничего не продолжит само — " +
                                    "процессы нужно будет проверить и возобновить вручную.\n\nЗакрыть Pepa?";
                var question = $"Сейчас работают или ждут процессы: {active}.\n\n{text}";
                var answer = IsVisible
                    ? MessageBox.Show(this, question, "Pepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    : MessageBox.Show(question, "Pepa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes || _closeAllowed || _exit is not null)
                {
                    return;
                }
            }
        }
        finally
        {
            _askingExit = false;
        }

        _exit = StopAndShutdownAsync();
        await _exit;
    }

    /// <summary>Остановить движок (дождаться выполняющихся шагов) и закрыть приложение — что бы ни случилось по дороге.</summary>
    private async Task StopAndShutdownAsync()
    {
        Hide();
        Tray?.SetExiting();
        try
        {
            // Остановка идёт в пуле потоков: её продолжения не зависят от этого окна.
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Pepa закрылась с ошибкой: {ex.Message}\n\nНезавершённые шаги разберёт восстановление при следующем запуске.",
                "Pepa", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _closeAllowed = true;
            Stopped?.Invoke();
            // Shutdown сам закрывает окна; Close() здесь не нужен и мог бы упасть, если окно посреди закрытия.
            Application.Current.Shutdown(0);
        }
    }

    /// <summary>Pepa остановлена выходом из меню или крестиком (перед закрытием приложения).</summary>
    public event Action? Stopped;

    // ───────────── Внешний вид ─────────────

    private void ShowProblem(string title, string text)
    {
        Web.Visibility = Visibility.Hidden;
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderTitle.Text = title;
        PlaceholderText.Text = text;
        PlaceholderActions.Visibility = Visibility.Visible;
    }

    private void OnInstallWebView(object sender, RoutedEventArgs e) => OpenExternal(WebViewDownloadUrl);

    private void OnOpenInBrowser(object sender, RoutedEventArgs e) => OpenExternal(_host.LaunchUrl.ToString());

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true });
        }
    }

    private void ApplyTheme()
    {
        var background = _dark ? Color.FromRgb(14, 16, 21) : Color.FromRgb(245, 246, 248);
        var foreground = _dark ? Color.FromRgb(232, 234, 238) : Color.FromRgb(16, 24, 40);
        Background = new SolidColorBrush(background);
        PlaceholderTitle.Foreground = new SolidColorBrush(foreground);
        PlaceholderText.Foreground = new SolidColorBrush(foreground);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar(bool dark)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var value = dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ и Windows 11). На старых версиях вызов просто не действует.
        _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
    }
}
