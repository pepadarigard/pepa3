using System.Diagnostics;
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
using Pepa.Host.Startup;

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

    /// <summary>Перехват завершения сеанса Windows; поле держит делегат, который вызывает Windows.</summary>
    private SessionEnd? _sessionEnd;

    /// <summary>Блокировка и разблокировка компьютера (и проверка при запуске) — по одной, в порядке прихода.</summary>
    private readonly SemaphoreSlim _lockGate = new(1, 1);

    /// <summary>Пока Pepa закрывалась, её запустили снова (ярлык): после закрытия — открыть заново.</summary>
    private bool _relaunch;

    /// <summary>Сколько ждать движок при завершении сеанса Windows: дольше Windows не ждёт.</summary>
    private static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(4);

    /// <summary>Аргумент перезапуска: подождать, пока прежняя Pepa (этот номер процесса) закроется совсем.</summary>
    private const string AfterArgument = "--after";

    /// <summary>Запуск платформы (в пуле потоков): завершение сеанса во время запуска дожидается его и останавливает.</summary>
    private Task<PepaHost>? _starting;

    /// <summary>Сеанс Windows закончился (или Pepa закрыла программа обновления): дальше не запускаться.</summary>
    private bool _sessionEnded;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Первым делом: перехватить вопрос Windows о завершении сеанса раньше WPF — он закрыл бы Pepa на вопросе (и посреди
        // запуска — без остановки движка). Ожидания ниже (UI Automation, прежняя Pepa) обрабатывают сообщения окна.
        _sessionEnd = SessionEnd.TryAttach(closeApp =>
        {
            if (closeApp)
            {
                OnClosedByRestartManager();
            }
            else
            {
                OnSessionEnded();
            }
        });
        if (_sessionEnd is null)
        {
            // Перехватить вопрос не удалось — как раньше: остановиться на вопросе (WPF всё равно закроет Pepa).
            SessionEnding += (_, _) => OnSessionEnded();
        }

        WaitForPreviousInstance(e.Args);

        _customDataDirectory = AutostartEntry.DataDirectoryFrom(e.Args);
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
        if (_sessionEnded)
        {
            return;
        }

        try
        {
            var options = new PepaHostOptions
            {
                DataDirectory = _dataDirectory,
                IsDesktop = true,
                Desktop = desktop,
                DesktopInput = desktopInput,
                DesktopAlerts = alerts,
                // Заблокирован уже при запуске — мышь и клавиатура у Pepa раньше, чем движок что-то выполнит.
                IsSessionLocked = SessionLock.IsLocked,
            };
            // В пуле потоков: если сеанс закончится во время запуска, поток окна дождётся запуска и остановит Pepa.
            _starting = Task.Run(() => PepaHost.StartAsync(options));
            _host = await _starting;
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
            if (_sessionEnded)
            {
                return;
            }

            MessageBox.Show(
                $"Не удалось запустить Pepa:\n\n{ex.Message}\n\nПодробности — в журнале: {Path.Combine(_dataDirectory, "logs")}",
                "Pepa", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (_sessionEnded)
        {
            // Сеанс закончился, пока Pepa запускалась: остановку уже сделал обработчик завершения.
            return;
        }

        SessionEnd.RegisterRestart(AutostartEntry.Arguments(_customDataDirectory));

        var settings = _settings = DesktopSettings.Load(_dataDirectory);
        var window = new MainWindow(_host, settings);
        MainWindow = window;
        var tray = _tray = new TrayIcon(
            open: () => ShowWindow(window, relaunchIfExiting: false),
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

        // Отметки — без обработчиков: иначе каждый запуск снова писал бы в реестр (а при ошибке записи — без конца).
        tray.SetChecks(settings.CloseAction == CloseAction.Tray, autostart);
        window.Tray = tray;
        alerts.Attach(window, tray);
        window.Closed += (_, _) => Shutdown(0);
        window.Stopped += () =>
        {
            if (_relaunch)
            {
                Relaunch();
            }
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        // Компьютер мог быть заблокирован уже при запуске (Windows сама вошла и заблокировала после обновления, автозапуск
        // запустил Pepa) — события блокировки тогда не будет.
        await SyncLockAtStartupAsync();
        StartTrayUpdates(tray);
        if (!minimized)
        {
            window.Show();
        }
        else if (_host.StartupMessage is { } message)
        {
            // Свёрнутая при запуске Pepa видна только значком: о процессах, остановившихся без неё, — сообщение у значка
            // (уведомления в Telegram могут быть выключены).
            tray.ShowBalloon("Pepa запущена", message);
        }

        ListenForActivation(activationName, window);
    }

    /// <summary>
    /// Сеанс Windows действительно заканчивается (WM_ENDSESSION): остановить движок за то время, что даёт Windows, и
    /// закрыться. Незавершённые шаги разберёт восстановление при следующем запуске.
    /// </summary>
    private void OnSessionEnded()
    {
        _sessionEnded = true;
        // Сеанс заканчивается — открывать Pepa снова некуда.
        _relaunch = false;
        _sessionEnd?.SetBlockReason("Pepa останавливает процессы…");
        (MainWindow as MainWindow)?.AllowClose();
        var spent = Stopwatch.StartNew();
        var host = _host;
        try
        {
            // Сеанс закончился посреди запуска — дождаться запуска (он в пуле потоков) и остановить.
            if (host is null && _starting is { } starting && starting.Wait(SessionEndBudget))
            {
                host = starting.Result;
            }

            if (host is not null)
            {
                // Остановка идёт в пуле потоков (PepaHost.StopAsync), поток окна только ждёт.
                var left = SessionEndBudget - spent.Elapsed;
                host.StopAsync().Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
        }
        catch (Exception)
        {
            // Запуск или остановка с ошибкой — сеанс всё равно заканчивается; разберёт восстановление.
        }

        Shutdown(0);
    }

    /// <summary>
    /// Pepa закрывает программа обновления (Restart Manager): сеанс продолжается, и Windows не обрывает Pepa сразу, — обычный
    /// выход, с ожиданием выполняющихся шагов, без вопросов. Обработчик окна возвращается сразу. После обновления Windows
    /// запустит Pepa снова (RegisterApplicationRestart).
    /// </summary>
    private void OnClosedByRestartManager()
    {
        _relaunch = false;
        _sessionEnd?.SetBlockReason("Pepa останавливает процессы…");
        if (MainWindow is MainWindow window)
        {
            window.ExitWithoutQuestions();
        }
        else
        {
            // Окна ещё нет (идёт запуск) — как при завершении сеанса.
            OnSessionEnded();
        }
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

    /// <param name="relaunchIfExiting">Pepa закрывается, а её запустили снова (ярлык) — открыть заново после закрытия.</param>
    private void ShowWindow(MainWindow window, bool relaunchIfExiting)
    {
        if (window.IsExiting)
        {
            // Окно не возвращается посреди остановки: крестик в нём оборвал бы её на полпути.
            _relaunch |= relaunchIfExiting;
            _tray?.ShowBalloon("Pepa закрывается", relaunchIfExiting
                ? "Подождите несколько секунд: Pepa дожидается выполняющихся шагов и откроется снова."
                : "Подождите несколько секунд: Pepa дожидается выполняющихся шагов.");
            return;
        }

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
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            // Сначала вернуть отметку (без обработчиков — реестр больше не трогаем), потом сказать.
            _tray?.SetChecks(_settings?.CloseAction == CloseAction.Tray, !enabled);
            MessageBox.Show($"Не удалось изменить запуск вместе с Windows: {ex.Message}", "Pepa", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                tray.SetText((await host.CountsAsync()).Text());
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
    /// разблокировки в очереди, а не падают (D-086). Если их сейчас держит процесс, его начатые блоки идут дальше, а после
    /// него ввод перейдёт к Pepa (заявка), а не к следующему в очереди — до разблокировки.
    /// </summary>
    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
        {
            await SetLockedAsync(e.Reason == SessionSwitchReason.SessionLock);
        }
    }

    private async Task SyncLockAtStartupAsync()
    {
        await _lockGate.WaitAsync();
        try
        {
            // Заблокированный при запуске компьютер движок уже отметил сам (до первых шагов). Здесь — сверка в обе стороны:
            // разблокировку между запуском движка и подпиской на события иначе никто бы не заметил. Под тем же замком, что и
            // события: разблокировка между проверкой и заявкой придёт следом.
            await ApplyLockAsync(SessionLock.IsLocked());
        }
        finally
        {
            _lockGate.Release();
        }
    }

    private async Task SetLockedAsync(bool locked)
    {
        await _lockGate.WaitAsync();
        try
        {
            await ApplyLockAsync(locked);
        }
        finally
        {
            _lockGate.Release();
        }
    }

    private async Task ApplyLockAsync(bool locked)
    {
        if (_host is not { } host)
        {
            return;
        }

        var input = DesktopNodes.InputResource;
        try
        {
            if (locked)
            {
                await host.Engine.ClaimExternallyAsync(input.Key, input.Title, ProcessEngine.LockedHolder);
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
                    window.Dispatcher.BeginInvoke(() => ShowWindow((MainWindow)window, relaunchIfExiting: true));
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

    /// <summary>Запустить Pepa снова (её открыли, пока она закрывалась): новая подождёт, пока эта закроется совсем.</summary>
    private void Relaunch()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(AfterArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (_customDataDirectory is { } data)
            {
                start.ArgumentList.Add(AutostartEntry.DataDirectoryArgument);
                start.ArgumentList.Add(data);
            }

            Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            MessageBox.Show($"Pepa закрыта, но открыть её снова не удалось: {ex.Message}\n\nЗапустите Pepa ещё раз.", "Pepa",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Перезапуск: прежняя Pepa ещё закрывается (её окно WebView2 держит папку данных интерфейса) — подождать её, но не
    /// бесконечно.
    /// </summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        var index = Array.IndexOf(args, AfterArgument);
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var id))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(id);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Уже закрылась.
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..16];
}
