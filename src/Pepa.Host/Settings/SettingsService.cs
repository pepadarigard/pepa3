using Pepa.Core.Serialization;
using Pepa.Engine;
using Pepa.Engine.Persistence;

namespace Pepa.Host.Settings;

public sealed record PlatformSettings
{
    /// <summary>Единственная папка, с которой работают файловые действия.</summary>
    public string WorkspaceRoot { get; init; } = "";

    public int MaxParallelProcesses { get; init; } = 4;
    public int StepBudget { get; init; } = 10_000;
    public int ActionTimeoutSeconds { get; init; } = 120;

    /// <summary>Адрес Local API AdsPower (смотрите в AdsPower: Настройки → Local API).</summary>
    public string AdsPowerUrl { get; init; } = DefaultAdsPowerUrl;

    /// <summary>Ключ API AdsPower, если в AdsPower включена проверка ключа. Хранится в базе Pepa на этом компьютере.</summary>
    public string? AdsPowerApiKey { get; init; }

    /// <summary>Сообщать в Telegram («Избранное» своего аккаунта), когда процесс ждёт решения.</summary>
    public bool NotifyTelegram { get; init; }

    public const string DefaultAdsPowerUrl = "http://127.0.0.1:50325";

    public EngineOptions ToEngineOptions() => new()
    {
        MaxParallelProcesses = MaxParallelProcesses,
        DefaultStepBudget = StepBudget,
        DefaultActionTimeout = TimeSpan.FromSeconds(ActionTimeoutSeconds),
    };
}

public sealed class SettingsValidationException(string message) : Exception(message);

/// <summary>Настройки платформы в хранилище. Рабочая папка применяется сразу, параметры движка — после перезапуска.</summary>
public sealed class SettingsService
{
    private const string Key = "platform";
    private readonly IPepaStore _store;
    private readonly string _dataDirectory;
    private PlatformSettings _current;

    private SettingsService(IPepaStore store, string dataDirectory, PlatformSettings current)
    {
        _store = store;
        _dataDirectory = dataDirectory;
        _current = current;
        Applied = current;
    }

    public PlatformSettings Current => Volatile.Read(ref _current);

    /// <summary>Значения, с которыми запущен движок.</summary>
    public PlatformSettings Applied { get; }

    public bool RestartRequired
    {
        get
        {
            var current = Current;
            return current.MaxParallelProcesses != Applied.MaxParallelProcesses ||
                   current.StepBudget != Applied.StepBudget ||
                   current.ActionTimeoutSeconds != Applied.ActionTimeoutSeconds;
        }
    }

    public static async Task<SettingsService> LoadAsync(IPepaStore store, string dataDirectory, string defaultWorkspace)
    {
        var all = await store.GetSettingsAsync();
        PlatformSettings settings;
        if (all.TryGetValue(Key, out var json))
        {
            settings = PepaJson.Deserialize<PlatformSettings>(json);
            if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            {
                settings = settings with { WorkspaceRoot = defaultWorkspace };
            }
        }
        else
        {
            // Первый запуск: сразу сохраняем выбранную рабочую папку, чтобы при следующих запусках
            // она не поменялась молча (например, если сервер запущен без --workspace).
            settings = new PlatformSettings { WorkspaceRoot = Path.GetFullPath(defaultWorkspace) };
            await using var write = await store.BeginWriteAsync();
            await write.SetSettingAsync(Key, PepaJson.Serialize(settings));
            await write.CommitAsync();
        }

        return new SettingsService(store, dataDirectory, settings);
    }

    public async Task<PlatformSettings> SaveAsync(PlatformSettings settings)
    {
        var normalized = Validate(settings);
        await using (var write = await _store.BeginWriteAsync())
        {
            await write.SetSettingAsync(Key, PepaJson.Serialize(normalized));
            await write.CommitAsync();
        }

        Volatile.Write(ref _current, normalized);
        return normalized;
    }

    private PlatformSettings Validate(PlatformSettings settings)
    {
        var root = settings.WorkspaceRoot?.Trim() ?? "";
        if (!Path.IsPathFullyQualified(root))
        {
            throw new SettingsValidationException("Рабочая папка: укажите полный путь, например C:\\Users\\Имя\\Documents\\Pepa Workspace.");
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var driveRoot = Path.GetPathRoot(root);
        if (driveRoot is not null && Path.TrimEndingDirectorySeparator(driveRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsValidationException("Рабочая папка не может быть корнем диска — выберите отдельную папку.");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            Path.TrimEndingDirectorySeparator(profile).Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsValidationException(
                "Рабочая папка не может быть всей папкой пользователя — выберите отдельную папку, например «Документы\\Pepa Workspace».");
        }

        // Рабочая папка не должна содержать базу Pepa: иначе сценарий мог бы испортить собственную историю.
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataDirectory));
        if (IsSameOrInside(data, root))
        {
            throw new SettingsValidationException(
                $"Рабочая папка не может совпадать с папкой данных Pepa («{data}») или содержать её.");
        }

        foreach (var forbidden in SystemFolders())
        {
            if (IsSameOrInside(root, forbidden) || IsSameOrInside(forbidden, root))
            {
                throw new SettingsValidationException(
                    $"Рабочая папка не может совпадать с системной папкой «{forbidden}», находиться внутри неё или содержать её.");
            }
        }

        if (File.Exists(root))
        {
            throw new SettingsValidationException("По указанному пути находится файл, а не папка.");
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsValidationException($"Не удалось создать рабочую папку: {ex.Message}");
        }

        var adsUrl = string.IsNullOrWhiteSpace(settings.AdsPowerUrl) ? PlatformSettings.DefaultAdsPowerUrl : settings.AdsPowerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(adsUrl, UriKind.Absolute, out var adsUri) || adsUri.Scheme is not ("http" or "https"))
        {
            throw new SettingsValidationException("Адрес AdsPower: укажите адрес вида http://127.0.0.1:50325 (его видно в AdsPower: Настройки → Local API).");
        }

        settings = settings with
        {
            AdsPowerUrl = adsUrl,
            AdsPowerApiKey = string.IsNullOrWhiteSpace(settings.AdsPowerApiKey) ? null : settings.AdsPowerApiKey.Trim(),
        };
        Check(settings.MaxParallelProcesses, 1, 32, "Одновременных процессов");
        Check(settings.StepBudget, 100, 10_000_000, "Лимит шагов");
        Check(settings.ActionTimeoutSeconds, 1, 86_400, "Тайм-аут действия");
        return settings with { WorkspaceRoot = root };
    }

    private static IEnumerable<string> SystemFolders()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.System,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                yield return Path.TrimEndingDirectorySeparator(path);
            }
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(folder, comparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, comparison);
    }

    private static void Check(int value, int min, int max, string title)
    {
        if (value < min || value > max)
        {
            throw new SettingsValidationException($"{title}: допустимо от {min} до {max}.");
        }
    }
}
