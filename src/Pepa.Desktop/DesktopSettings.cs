using System.IO;
using System.Text.Json;

namespace Pepa.Desktop;

/// <summary>Что делать, когда закрывают окно Pepa.</summary>
public enum CloseAction
{
    /// <summary>Спросить при первом закрытии.</summary>
    Ask,

    /// <summary>Свернуть в область уведомлений: процессы работают дальше.</summary>
    Tray,

    /// <summary>Завершить Pepa (процессы приостанавливаются до ручного возобновления).</summary>
    Exit,
}

/// <summary>Настройки окна Pepa (D-086) — в папке данных, файл desktop.json. Сбой чтения — значения по умолчанию.</summary>
public sealed class DesktopSettings
{
    private readonly string _path;

    private DesktopSettings(string path, Stored stored)
    {
        _path = path;
        CloseAction = stored.CloseAction;
    }

    public CloseAction CloseAction { get; set; }

    // «Запускать вместе с Windows» здесь не хранится: правда — в реестре (её может изменить и сама Windows), копия здесь
    // устаревала бы и возвращала выключенный автозапуск.
    private sealed record Stored(CloseAction CloseAction = CloseAction.Ask);

    public static DesktopSettings Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "desktop.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) is { } stored)
            {
                return new DesktopSettings(path, stored);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый файл — как при первом запуске: спросить снова лучше, чем молча решить за пользователя.
        }

        return new DesktopSettings(path, new Stored());
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(CloseAction)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не сохранилось — в следующий раз спросим снова; работе это не мешает.
        }
    }
}
