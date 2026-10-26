using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Pepa.Adapters.Browser;

public enum BrowserKind
{
    /// <summary>Браузер, запущенный Pepa (Edge или Chrome).</summary>
    Local,

    /// <summary>Браузер профиля AdsPower, к которому Pepa подключилась.</summary>
    AdsPower,
}

/// <summary>Открытый браузер, с которым работает процесс.</summary>
public sealed class BrowserSession
{
    internal BrowserSession(string processId, string name, BrowserKind kind, IBrowserContext context, IBrowser? browser,
        IPage page, BrowserSessionInfo info)
    {
        ProcessId = processId;
        Name = name;
        Kind = kind;
        Context = context;
        Browser = browser;
        Page = page;
        Info = info;
        context.Close += (_, _) => Closed = true;
        if (browser is not null)
        {
            browser.Disconnected += (_, _) => Closed = true;
        }
    }

    public string ProcessId { get; }
    public string Name { get; }
    public BrowserKind Kind { get; }
    public IBrowserContext Context { get; }
    public IBrowser? Browser { get; }
    public IPage Page { get; internal set; }
    public BrowserSessionInfo Info { get; }
    public bool Closed { get; private set; }

    /// <summary>Текущая вкладка; если её закрыли — последняя открытая.</summary>
    public IPage CurrentPage()
    {
        if (!Page.IsClosed)
        {
            return Page;
        }

        Page = Context.Pages.LastOrDefault(p => !p.IsClosed)
               ?? throw new BrowserSessionLostException($"Все вкладки браузера «{Name}» закрыты.");
        return Page;
    }
}

/// <summary>Что нужно помнить о сессии между запусками Pepa (сам браузер при перезапуске Pepa не сохраняется).</summary>
public sealed record BrowserSessionInfo
{
    public required string ProcessId { get; init; }
    public required string Name { get; init; }
    public required BrowserKind Kind { get; init; }
    public string? ProfileId { get; init; }
    public string? ResourceKey { get; init; }

    /// <summary>Закрыть профиль AdsPower (или браузер), когда процесс завершится.</summary>
    public bool CloseOnFinish { get; init; } = true;
}

/// <summary>Сессия потеряна: браузер закрыт или Pepa перезапускалась. Действие при этом не выполнялось.</summary>
public sealed class BrowserSessionLostException(string message) : Exception(message);

/// <summary>Параметры запуска обычного браузера.</summary>
public sealed record LocalBrowserOptions
{
    /// <summary>«msedge» или «chrome».</summary>
    public string Channel { get; init; } = "msedge";
    public bool Headless { get; init; }

    /// <summary>Папка профиля (вход на сайты сохраняется); null — временный профиль.</summary>
    public string? UserDataDir { get; init; }
}

/// <summary>Общие параметры модуля браузера.</summary>
public sealed record BrowserModuleOptions
{
    /// <summary>Папка данных Pepa: здесь список сессий и профили браузера Pepa.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Свой исполняемый файл браузера вместо Edge/Chrome (для проверки и разработки).</summary>
    public string? ExecutablePathOverride { get; init; }
}

/// <summary>
/// Открытые браузеры процессов. Один экземпляр Playwright на всё приложение; у каждого процесса — свои сессии по имени.
/// Список сессий сохраняется на диск: после перезапуска Pepa к ещё открытому профилю AdsPower можно подключиться снова
/// (это только подключение, ничего не запускается), а потерянный обычный браузер честно считается закрытым.
/// </summary>
public sealed class BrowserSessions : IAsyncDisposable
{
    private readonly BrowserModuleOptions _options;
    private readonly AdsPowerClient _adsPower;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(string, string), BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _registryPath;
    private Dictionary<string, BrowserSessionInfo> _registry;
    private IPlaywright? _playwright;

    public BrowserSessions(BrowserModuleOptions options, AdsPowerClient adsPower, ILogger? logger = null)
    {
        _options = options;
        _adsPower = adsPower;
        _logger = logger ?? NullLogger.Instance;
        _registryPath = Path.Combine(options.DataDirectory, "browser-sessions.json");
        _registry = LoadRegistry(_registryPath);
    }

    public AdsPowerClient AdsPower => _adsPower;

    public string ProfilesDirectory => Path.Combine(_options.DataDirectory, "browser-profiles");

    private static string Key(string processId, string name) => $"{processId}/{name}";

    // ───────────── Открытие ─────────────

    public async Task<BrowserSession> OpenLocalAsync(string processId, string name, LocalBrowserOptions options, string? resourceKey,
        bool closeOnFinish, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue((processId, name), out var existing) && !existing.Closed)
            {
                return existing;
            }

            var playwright = await PlaywrightAsync();
            var launch = new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = _options.ExecutablePathOverride is null ? options.Channel : null,
                ExecutablePath = _options.ExecutablePathOverride,
                Headless = options.Headless,
                ViewportSize = ViewportSize.NoViewport,
            };

            IBrowser? browser = null;
            IBrowserContext context;
            try
            {
                if (options.UserDataDir is { } dir)
                {
                    Directory.CreateDirectory(dir);
                    context = await playwright.Chromium.LaunchPersistentContextAsync(dir, launch);
                }
                else
                {
                    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Channel = launch.Channel,
                        ExecutablePath = launch.ExecutablePath,
                        Headless = options.Headless,
                    });
                    context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = ViewportSize.NoViewport });
                }
            }
            catch (PlaywrightException ex)
            {
                throw new BrowserStartException(DescribeLaunchError(options.Channel, ex));
            }

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            var info = new BrowserSessionInfo
            {
                ProcessId = processId, Name = name, Kind = BrowserKind.Local, ResourceKey = resourceKey, CloseOnFinish = closeOnFinish,
            };
            var session = new BrowserSession(processId, name, BrowserKind.Local, context, browser, page, info);
            _sessions[(processId, name)] = session;
            SaveRegistry(info);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Открыть профиль AdsPower (или подключиться к уже открытому) и подключить к нему Playwright.</summary>
    public async Task<BrowserSession> OpenAdsPowerAsync(string processId, string name, string profileId, bool newTab, bool closeOnFinish,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue((processId, name), out var existing) && !existing.Closed && existing.Info.ProfileId == profileId)
            {
                return existing;
            }

            var state = await _adsPower.GetStateAsync(profileId, cancellationToken);
            if (!state.Active || state.WsEndpoint is null)
            {
                state = await _adsPower.StartAsync(profileId, cancellationToken);
            }

            var info = new BrowserSessionInfo
            {
                ProcessId = processId, Name = name, Kind = BrowserKind.AdsPower, ProfileId = profileId,
                ResourceKey = AdsPowerResourceKey(profileId), CloseOnFinish = closeOnFinish,
            };
            SaveRegistry(info);
            var session = await ConnectAsync(info, state.WsEndpoint!, newTab);
            _sessions[(processId, name)] = session;
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string AdsPowerResourceKey(string profileId) => $"adspower:{profileId}";

    private async Task<BrowserSession> ConnectAsync(BrowserSessionInfo info, string endpoint, bool newTab)
    {
        var playwright = await PlaywrightAsync();
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint, new BrowserTypeConnectOverCDPOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException ex)
        {
            throw new BrowserStartException($"Профиль AdsPower {info.ProfileId} открыт, но подключиться к его браузеру не удалось: {ex.Message}");
        }

        var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
        var page = newTab ? await context.NewPageAsync() : context.Pages.LastOrDefault(p => !p.IsClosed) ?? await context.NewPageAsync();
        return new BrowserSession(info.ProcessId, info.Name, BrowserKind.AdsPower, context, browser, page, info);
    }

    // ───────────── Поиск ─────────────

    /// <summary>Открытая сессия процесса. Профиль AdsPower, открытый до перезапуска Pepa, подключается снова.</summary>
    public async Task<BrowserSession> GetAsync(string processId, string name, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue((processId, name), out var session) && !session.Closed)
        {
            return session;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue((processId, name), out session) && !session.Closed)
            {
                return session;
            }

            if (!_registry.TryGetValue(Key(processId, name), out var info))
            {
                throw new BrowserSessionLostException(
                    $"Браузер «{name}» не открыт в этом процессе. Добавьте перед этим блоком «Открыть браузер» или «Открыть профиль AdsPower».");
            }

            if (info.Kind == BrowserKind.Local)
            {
                throw new BrowserSessionLostException(
                    $"Браузер «{name}» был закрыт (закрыли окно или перезапускалась Pepa) — открытая страница потеряна. " +
                    "Откройте браузер снова блоком «Открыть браузер».");
            }

            var state = await _adsPower.GetStateAsync(info.ProfileId!, cancellationToken);
            if (!state.Active || state.WsEndpoint is null)
            {
                throw new BrowserSessionLostException(
                    $"Профиль AdsPower {info.ProfileId} закрыт. Откройте его снова блоком «Открыть профиль AdsPower».");
            }

            session = await ConnectAsync(info, state.WsEndpoint, newTab: false);
            _sessions[(processId, name)] = session;
            _logger.LogInformation("Переподключение к профилю AdsPower {Profile} для процесса {Process}.", info.ProfileId, processId);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Есть ли у процесса живая сессия с таким именем (без переподключения).</summary>
    public bool IsOpen(string processId, string name) =>
        _sessions.TryGetValue((processId, name), out var session) && !session.Closed;

    public BrowserSessionInfo? FindInfo(string processId, string name) => _registry.GetValueOrDefault(Key(processId, name));

    // ───────────── Закрытие ─────────────

    /// <summary>Закрыть сессию: обычный браузер закрывается; профиль AdsPower — по желанию (иначе только отключение).</summary>
    public async Task<BrowserSessionInfo?> CloseAsync(string processId, string name, bool stopProfile, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _sessions.TryRemove((processId, name), out var session);
            var info = session?.Info ?? _registry.GetValueOrDefault(Key(processId, name));
            if (session is not null)
            {
                await CloseQuietlyAsync(session);
            }

            if (info is { Kind: BrowserKind.AdsPower, ProfileId: { } profileId } && stopProfile)
            {
                await _adsPower.StopAsync(profileId, cancellationToken);
            }

            RemoveRegistry(processId, name);
            return info;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Процесс завершился: закрыть его браузеры (профили AdsPower — если так настроено при открытии).</summary>
    public async Task ReleaseProcessAsync(string processId)
    {
        var names = _registry.Values.Where(i => i.ProcessId == processId).Select(i => (i.Name, i.CloseOnFinish))
            .Concat(_sessions.Keys.Where(k => k.Item1 == processId).Select(k => (k.Item2, true)))
            .DistinctBy(n => n.Item1)
            .ToList();
        foreach (var (name, closeOnFinish) in names)
        {
            try
            {
                var info = FindInfo(processId, name);
                await CloseAsync(processId, name, stopProfile: info?.CloseOnFinish ?? closeOnFinish, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось закрыть браузер «{Name}» процесса {Process}.", name, processId);
            }
        }
    }

    /// <summary>При запуске Pepa: забыть сессии процессов, которые уже не активны. Внешних действий не выполняется.</summary>
    public void Prune(IReadOnlyCollection<string> activeProcessIds)
    {
        var active = activeProcessIds.ToHashSet(StringComparer.Ordinal);
        lock (_registry)
        {
            var stale = _registry.Where(e => !active.Contains(e.Value.ProcessId)).Select(e => e.Key).ToList();
            foreach (var key in stale)
            {
                _registry.Remove(key);
            }

            if (stale.Count > 0)
            {
                WriteRegistry();
            }
        }
    }

    private static async Task CloseQuietlyAsync(BrowserSession session)
    {
        try
        {
            if (session.Kind == BrowserKind.Local)
            {
                await session.Context.CloseAsync();
            }

            if (session.Browser is not null)
            {
                // Для профиля AdsPower это только отключение: браузер профиля остаётся открытым.
                await session.Browser.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
            // Браузер уже закрыт.
        }
    }

    private async Task<IPlaywright> PlaywrightAsync() => _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();

    private static string DescribeLaunchError(string channel, PlaywrightException ex)
    {
        var name = channel == "chrome" ? "Google Chrome" : "Microsoft Edge";
        return ex.Message.Contains("is not found", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            ? $"{name} не найден на этом компьютере. Установите его или выберите другой браузер в блоке."
            : $"Не удалось запустить {name}: {FirstLine(ex.Message)}";
    }

    internal static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    // ───────────── Список сессий на диске ─────────────

    private static Dictionary<string, BrowserSessionInfo> LoadRegistry(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var items = JsonSerializer.Deserialize<List<BrowserSessionInfo>>(File.ReadAllText(path), RegistryJson) ?? [];
                return items.ToDictionary(i => Key(i.ProcessId, i.Name));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый список — начинаем с пустого (сессии браузеров в любом случае не переживают перезапуск).
        }

        return [];
    }

    private void SaveRegistry(BrowserSessionInfo info)
    {
        lock (_registry)
        {
            _registry[Key(info.ProcessId, info.Name)] = info;
            WriteRegistry();
        }
    }

    private void RemoveRegistry(string processId, string name)
    {
        lock (_registry)
        {
            if (_registry.Remove(Key(processId, name)))
            {
                WriteRegistry();
            }
        }
    }

    private void WriteRegistry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
        var temp = _registryPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_registry.Values.ToList(), RegistryJson));
        File.Move(temp, _registryPath, overwrite: true);
    }

    private static readonly JsonSerializerOptions RegistryJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await CloseQuietlyAsync(session);
        }

        _sessions.Clear();
        _playwright?.Dispose();
        _lock.Dispose();
    }
}

/// <summary>Браузер не запустился или к нему не удалось подключиться — действие на странице не выполнялось.</summary>
public sealed class BrowserStartException(string message) : Exception(message);
