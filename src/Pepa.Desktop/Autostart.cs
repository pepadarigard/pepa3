using System.IO;
using Microsoft.Win32;

namespace Pepa.Desktop;

/// <summary>
/// «Запускать вместе с Windows» (D-086): запись в HKCU\…\Run — без прав администратора, только для этого пользователя.
/// Pepa запускается свёрнутой в область уведомлений. Путь к программе обновляется при каждом запуске: папку с Pepa могли
/// перенести.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string MinimizedArgument = "--minimized";

    /// <param name="dataDirectory">Своя папка данных (не по умолчанию) — своя запись: несколько Pepa не мешают друг другу.</param>
    private static string ValueName(string? dataDirectory) => dataDirectory is null ? "Pepa" : $"Pepa ({Path.GetFileName(dataDirectory)})";

    private static string Command(string? dataDirectory)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось узнать путь к Pepa.exe.");
        return $"\"{exe}\" {MinimizedArgument}" + (dataDirectory is null ? "" : $" --data-dir \"{dataDirectory}\"");
    }

    public static bool IsEnabled(string? dataDirectory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName(dataDirectory)) is string;
    }

    public static void Set(bool enabled, string? dataDirectory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName(dataDirectory), Command(dataDirectory));
        }
        else
        {
            key.DeleteValue(ValueName(dataDirectory), throwOnMissingValue: false);
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
}
