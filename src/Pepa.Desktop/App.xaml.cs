using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Pepa.Adapters.Desktop;
using Pepa.Adapters.Desktop.Windows;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _dataDirectory = ParseDataDirectory(e.Args) ?? _dataDirectory;
        var activationName = $"Local\\Pepa.Activate.{Hash(_dataDirectory)}";

        IDesktop? desktop = null;
        try
        {
            desktop = _desktop = new UiaDesktop();
        }
        catch (Exception ex)
        {
            // Без UI Automation блоки окон честно сообщат причину; остальная Pepa работает.
            desktop = new UnavailableDesktop($"UI Automation Windows недоступна: {ex.Message}");
        }

        try
        {
            _host = await PepaHost.StartAsync(new PepaHostOptions
            {
                DataDirectory = _dataDirectory,
                IsDesktop = true,
                Desktop = desktop,
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

        var window = new MainWindow(_host);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown(0);
        SessionEnding += (_, _) => window.CloseForSessionEnd();
        window.Show();
        ListenForActivation(activationName, window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Dispose();
        _desktop?.Dispose();
        base.OnExit(e);
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
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        if (window.WindowState == WindowState.Minimized)
                        {
                            window.WindowState = WindowState.Normal;
                        }

                        window.Activate();
                        window.Topmost = true;
                        window.Topmost = false;
                        window.Focus();
                    });
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
