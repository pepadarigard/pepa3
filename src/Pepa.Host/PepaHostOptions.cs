namespace Pepa.Host;

public sealed record PepaHostOptions
{
    /// <summary>Папка с базой, журналами и блокировкой экземпляра.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Порт на 127.0.0.1; 0 — любой свободный.</summary>
    public int Port { get; init; }

    /// <summary>Токен доступа к API; null — случайный при каждом запуске.</summary>
    public string? Token { get; init; }

    /// <summary>Папка собранного интерфейса; null — wwwroot рядом с приложением.</summary>
    public string? UiDirectory { get; init; }

    /// <summary>Рабочая папка по умолчанию (если в настройках ещё не задана).</summary>
    public string? DefaultWorkspaceRoot { get; init; }

    /// <summary>Создать примеры сценариев при первом запуске.</summary>
    public bool SeedSamples { get; init; } = true;

    /// <summary>Запрет второго экземпляра над той же папкой данных.</summary>
    public bool UseInstanceLock { get; init; } = true;

    /// <summary>Запущено из оконного приложения (интерфейс показывает нативные функции).</summary>
    public bool IsDesktop { get; init; }

    /// <summary>Писать журнал платформы в консоль (для сервера без окна).</summary>
    public bool ConsoleLogging { get; init; }

    /// <summary>Свой исполняемый файл браузера вместо Edge/Chrome (разработка и проверка без Windows).</summary>
    public string? BrowserExecutablePath { get; init; }

    /// <summary>Адрес сервера Telegram «ip:порт» вместо основного (проверки: чтобы не обращаться к настоящему Telegram).</summary>
    public string? TelegramServerAddress { get; init; }

    /// <summary>Сколько ждать ответа Telegram при подключении и входе.</summary>
    public TimeSpan TelegramConnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Окна Windows, мышь и клавиатура (реализация из оконного приложения). null — блоки окон есть в конструкторе,
    /// но при запуске честно сообщают, что работают только в приложении Pepa для Windows.
    /// </summary>
    public Adapters.Desktop.IDesktop? Desktop { get; init; }
}

public static class HostPaths
{
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create), "Pepa");

    public static string DefaultWorkspace(string dataDirectory)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents)
            ? Path.Combine(dataDirectory, "workspace")
            : Path.Combine(documents, "Pepa Workspace");
    }
}
