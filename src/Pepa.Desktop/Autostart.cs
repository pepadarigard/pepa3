using Microsoft.Win32;
using Pepa.Host.Startup;

namespace Pepa.Desktop;

/// <summary>
/// «Запускать вместе с Windows» (D-086): запись в HKCU\…\Run — без прав администратора, только для этого пользователя.
/// Pepa запускается свёрнутой в область уведомлений. Путь к программе обновляется при каждом запуске: папку с Pepa могли
/// перенести. Командную строку и имя записи строит <see cref="AutostartEntry"/>.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Здесь Windows отмечает записи, выключенные в «Автозагрузке» диспетчера задач или параметров.</summary>
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string MinimizedArgument = AutostartEntry.MinimizedArgument;

    /// <summary>Запись есть, запускает эту папку данных, и Windows её не выключила.</summary>
    public static bool IsEnabled(string? dataDirectory)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (FindValue(run, dataDirectory) is not { } name)
        {
            return false;
        }

        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        return AutostartEntry.IsApproved(approved?.GetValue(name) as byte[]);
    }

    /// <summary>
    /// Включить или выключить. Включение снимает и отметку «выключено» самой Windows — иначе галочка в меню стояла бы, а Pepa
    /// после перезагрузки не запускалась.
    /// </summary>
    public static void Set(bool enabled, string? dataDirectory)
    {
        var name = AutostartEntry.ValueName(dataDirectory);
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var previous = FindValue(run, dataDirectory);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось узнать путь к Pepa.exe.");
            run.SetValue(name, AutostartEntry.Command(exe, dataDirectory));
        }
        else
        {
            run.DeleteValue(name, throwOnMissingValue: false);
        }

        // Запись под прежним именем (по одному имени папки) — переносим на новое.
        if (previous is not null && previous != name)
        {
            run.DeleteValue(previous, throwOnMissingValue: false);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
        foreach (var value in new[] { name, previous }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            approved?.DeleteValue(value, throwOnMissingValue: false);
        }
    }

    /// <summary>Включён — переписать путь к программе (её могли перенести или обновить в другую папку).</summary>
    public static void Refresh(string? dataDirectory)
    {
        if (IsEnabled(dataDirectory))
        {
            Set(true, dataDirectory);
        }
    }

    /// <summary>
    /// Имя записи этой папки данных: нынешнее или прежнее (по одному имени папки). Чужая запись — другой папки с тем же
    /// именем — не считается и не переписывается.
    /// </summary>
    private static string? FindValue(RegistryKey? run, string? dataDirectory)
    {
        if (run is null)
        {
            return null;
        }

        foreach (var name in AutostartEntry.LegacyValueNames(dataDirectory).Prepend(AutostartEntry.ValueName(dataDirectory)))
        {
            if (run.GetValue(name) is string command && AutostartEntry.Starts(command, dataDirectory))
            {
                return name;
            }
        }

        return null;
    }
}
