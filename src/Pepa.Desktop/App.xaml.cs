using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Pepa.Adapters.Desktop;
using Pepa.Adapters.Desktop.Windows;
using Pepa.Engine;
using Pepa.Host;

namespace Pepa.Desktop;

/// <summary>
/// Оконное приложение: поднимает платформу (хранилище, движок, локальный API) в этом же процессе
/// и показывает интерфейс в WebView2.
/// </summary>
public partial class App : Application
{
    private PepaHost? _host;
    private UiaDesktop? _desktop;
    private EventWaitHandle? _activation;
    private string _dataDirectory = HostPaths.DefaultDataDirectory();

    /// <summary>Своя папка данных (--data-dir) — null, если по умолчанию: у автозапуска тогда своя запись.</summary>
    private string? _customDataDirectory;
    private TrayIcon? _tray;
    private DispatcherTimer? _trayTimer;
    private DesktopSettings? _settings;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _customDataDirectory = ParseDataDirectory(e.Args);
        _dataDirectory = _customDataDirectory ?? _dataDirectory;
        // Запуск вместе с Windows — сразу в область уведомлений, без окна.
        var minimized = e.Args.Contains(Autostart.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
        var activationName = $"Local\\Pepa.Activate.{Hash(_dataDirectory)}";

        IDesktop? desktop = null;
        IDesktopInputMonitor? desktopInput = null;
        try
        {
            desktop = _desktop = new UiaDesktop();
            desktopInput = new UiaInputMonitor();
        }
        catch (Exception ex)
        {
            // Без UI Automation блоки окон честно сообщат причину; остальная Pepa работает.
            desktop = new UnavailableDesktop($"UI Automation Windows недоступна: {ex.Message}");
        }

        var alerts = new WindowAlerts();
        try
        {
            _host = await PepaHost.StartAsync(new PepaHostOptions
            {
                DataDirectory = _dataDirectory,
                IsDesktop = true,
                Desktop = desktop,
                DesktopInput = desktopInput,
                DesktopAlerts = alerts,
            });
        }
        catch (InstanceAlreadyRunningException)
        {
            // Pepa уже открыта — показываем её окно вместо второго экземпляра.
            if (EventWaitHandle.TryOpenExisting(activationName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            else
            {
                MessageBox.Show("Pepa уже запущена.", "Pepa", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown(0);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось запустить Pepa:\n\n{ex.Message}\n\nПодробности — в журнале: {Path.Combine(_dataDirectory, "logs")}",
                "Pepa", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var settings = _settings = DesktopSettings.Load(_dataDirectory);
        var window = new MainWindow(_host, settings);
        MainWindow = window;
        var tray = _tray = new TrayIcon(
            open: () => ShowWindow(window),
            exit: window.ExitPepa,
            closeToTray: toTray =>
            {
                settings.CloseAction = toTray ? CloseAction.Tray : CloseAction.Exit;
                settings.Save();
            },
            startWithWindows: SetAutostart);
        var autostart = false;
        try
        {
            autostart = Autostart.IsEnabled(_customDataDirectory);
            Autostart.Refresh(_customDataDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Реестр недоступен — пункт меню просто не отмечен.
        }

        tray.SetChecks(settings.CloseAction == CloseAction.Tray, autostart);
        window.Tray = tray;
        alerts.Attach(window, tray);
        window.Closed += (_, _) => Shutdown(0);
        SessionEnding += (_, _) =>
        {
            window.CloseForSessionEnd();
            // Окно, свёрнутое при запуске, могло ни разу не открываться — тогда его закрытие движок не останавливает.
            if (_host is { } host)
            {
                Task.Run(() => host.StopAsync()).Wait(TimeSpan.FromSeconds(4));
            }
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        StartTrayUpdates(tray);
        if (!minimized)
        {
            window.Show();
        }

        ListenForActivation(activationName, window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _trayTimer?.Stop();
        _tray?.Dispose();
        _activation?.Dispose();
        _desktop?.Dispose();
        base.OnExit(e);
    }

    private static void ShowWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            Autostart.Set(enabled, _customDataDirectory);
            if (_settings is { } settings)
            {
                settings.StartWithWindows = enabled;
                settings.Save();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            MessageBox.Show($"Не удалось изменить запуск вместе с Windows: {ex.Message}", "Pepa", MessageBoxButton.OK, MessageBoxImage.Warning);
            _tray?.SetChecks(_settings?.CloseAction == CloseAction.Tray, !enabled);
        }
    }

    /// <summary>Подсказка над значком — что сейчас делается; раз в 15 секунд.</summary>
    private void StartTrayUpdates(TrayIcon tray)
    {
        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        async void Update()
        {
            if (_host is not { } host)
            {
                return;
            }

            try
            {
                var (active, attention) = await host.CountsAsync();
                tray.SetText(attention > 0
                    ? $"Pepa — ждут вашего решения: {attention}; работают или ждут: {active}"
                    : active > 0 ? $"Pepa — работают или ждут: {active}" : "Pepa — процессов нет");
            }
            catch (Exception)
            {
                // Подсказка — не главное: ошибка чтения (Pepa закрывается) не мешает.
            }
        }

        _trayTimer.Tick += (_, _) => Update();
        _trayTimer.Start();
        Update();
    }

    /// <summary>
    /// Компьютер заблокирован (Win+L): мышь и клавиатура достаются «Pepa: компьютер заблокирован» — блоки окон ждут
    /// разблокировки в очереди, а не падают (D-086). Если их сейчас держит процесс, его блоки и так в работе — ничего не
    /// трогаем.
    /// </summary>
    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_host is not { } host || e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock))
        {
            return;
        }

        var input = DesktopNodes.InputResource;
        try
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                await host.Engine.TryHoldExternallyAsync(input.Key, input.Title, ProcessEngine.LockedHolder);
            }
            else
            {
                await host.Engine.ReleaseExternalAsync(input.Key, ProcessEngine.LockedHolder);
            }
        }
        catch (Exception)
        {
            // Pepa закрывается или движок недоступен: блокировка этого сеанса уже не важна.
        }
    }

    private void ListenForActivation(string name, Window window)
    {
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        var handle = _activation;
        var thread = new Thread(() =>
        {
            try
            {
                while (handle.WaitOne())
                {
                    window.Dispatcher.BeginInvoke(() => ShowWindow(window));
                }
            }
            catch (ObjectDisposedException)
            {
                // Приложение закрывается.
            }
        })
        {
            IsBackground = true,
            Name = "Pepa activation",
        };
        thread.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logs = Path.Combine(_dataDirectory, "logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "desktop-errors.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Не удалось записать — показываем сообщение всё равно.
        }

        MessageBox.Show($"Непредвиденная ошибка окна: {e.Exception.Message}\n\nДвижок продолжает работать.",
            "Pepa", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static string? ParseDataDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return null;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..16];
}
