using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Browser;

public enum BrowserKind
{
    /// <summary>Браузер, запущенный Pepa (Edge или Chrome).</summary>
    Local,

    /// <summary>Браузер профиля AdsPower, к которому Pepa подключилась.</summary>
    AdsPower,
}

/// <summary>Как отвечать на окно сайта (alert, confirm, prompt, «Покинуть страницу?»).</summary>
public enum DialogAnswer
{
    /// <summary>Ответить «Отмена» (у простого сообщения — «OK», другого нет) и остановить процесс: решает пользователь.</summary>
    Stop,

    /// <summary>Согласиться: «OK» (в окне-вопросе — с заданным ответом).</summary>
    Accept,

    /// <summary>Отказаться: «Отмена».</summary>
    Dismiss,
}

/// <summary>Окно сайта: что спросил сайт и что ответила Pepa.</summary>
public sealed record SiteDialog(string Kind, string Message, string Answer, DateTimeOffset At);

/// <summary>Открытый браузер, с которым работает процесс.</summary>
public sealed class BrowserSession
{
    private readonly Lock _dialogGate = new();
    private readonly List<SiteDialog> _dialogs = [];
    private DialogAnswer _answer = DialogAnswer.Stop;
    private string? _promptText;

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

        // Окна сайта не закрываются молча: без обработчика Playwright отвечает «Отмена», а шаг считался бы выполненным.
        foreach (var open in context.Pages)
        {
            WatchDialogs(open);
        }

        context.Page += (_, opened) => WatchDialogs(opened);
    }

    public string ProcessId { get; }
    public string Name { get; }
    public BrowserKind Kind { get; }
    public IBrowserContext Context { get; }
    public IBrowser? Browser { get; }
    public IPage Page { get; internal set; }
    public BrowserSessionInfo Info { get; internal set; }
    public bool Closed { get; private set; }

    /// <summary>Окна отвечает человек (запись действий): Pepa их только видит.</summary>
    internal bool ManualDialogs { get; set; }

    private void WatchDialogs(IPage page) => page.Dialog += (_, dialog) => _ = AnswerAsync(dialog);

    private async Task AnswerAsync(IDialog dialog)
    {
        if (ManualDialogs)
        {
            return;
        }

        DialogAnswer answer;
        string? text;
        lock (_dialogGate)
        {
            answer = _answer;
            text = _promptText;
        }

        // У простого сообщения одна кнопка. Окно запоминается до ответа: ответ отпускает страницу, и шаг может закончиться
        // раньше, чем эта запись.
        var accept = answer == DialogAnswer.Accept || dialog.Type == DialogType.Alert;
        lock (_dialogGate)
        {
            _dialogs.Add(new SiteDialog(KindOf(dialog.Type), dialog.Message, accept ? "OK" : "Отмена", DateTimeOffset.Now));
        }

        try
        {
            if (accept)
            {
                await dialog.AcceptAsync(dialog.Type == DialogType.Prompt ? text ?? dialog.DefaultValue : null);
            }
            else
            {
                await dialog.DismissAsync();
            }
        }
        catch (PlaywrightException)
        {
            // Вкладка или браузер закрылись.
        }
    }

    private static string KindOf(string type) => type switch
    {
        DialogType.Alert => "сообщение",
        DialogType.Confirm => "вопрос",
        DialogType.Prompt => "запрос",
        DialogType.BeforeUnload => "«Покинуть страницу?»",
        _ => type,
    };

    /// <summary>Шаг начался: окна, которые покажет сайт, получат этот ответ.</summary>
    internal void BeginDialogs(DialogAnswer answer, string? promptText)
    {
        lock (_dialogGate)
        {
            _answer = answer;
            _promptText = promptText;
        }
    }

    /// <summary>Шаг закончился: окна между шагами — «Отмена» и остановка следующего шага.</summary>
    internal void EndDialogs()
    {
        lock (_dialogGate)
        {
            _answer = DialogAnswer.Stop;
            _promptText = null;
        }
    }

    /// <summary>Окна, показанные с прошлого раза (и забыть их).</summary>
    internal List<SiteDialog> TakeDialogs()
    {
        lock (_dialogGate)
        {
            var taken = _dialogs.ToList();
            _dialogs.Clear();
            return taken;
        }
    }

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

    /// <summary>
    /// Вкладка, в которой работает процесс (идентификатор вкладки в браузере): после перезапуска Pepa процесс продолжает в
    /// ней, а не в последней открытой — та могла быть вкладкой владельца профиля.
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>Процесс работает в своей вкладке чужого (открытого не Pepa) профиля: другие вкладки не его ни при каких условиях.</summary>
    public bool OwnTab { get; init; }
}

/// <summary>Что делать, если профиль AdsPower уже открыт не Pepa (например, владельцем вручную).</summary>
public enum AdsPowerOpenedElsewhere
{
    /// <summary>Не трогать чужое окно: шаг не выполняется.</summary>
    Stop,

    /// <summary>Работать в своей новой вкладке и не закрывать профиль в конце.</summary>
    NewTab,
}

/// <summary>Профиль AdsPower открыт не Pepa — работать в нём, не спросив, нельзя. Ничего не выполнялось.</summary>
public sealed class AdsPowerOpenedElsewhereException(string message) : Exception(message);

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

    /// <summary>Куда сохранять снимок страницы, когда шаг не удался; null — не снимать.</summary>
    public ISnapshotStore? Snapshots { get; init; }
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

    /// <summary>Профили AdsPower, которые запустила Pepa и ещё не закрыла (на диске: переживают перезапуск Pepa).</summary>
    private readonly string _startedPath;
    private readonly HashSet<string> _started;
    private IPlaywright? _playwright;

    public BrowserSessions(BrowserModuleOptions options, AdsPowerClient adsPower, ILogger? logger = null)
    {
        _options = options;
        _adsPower = adsPower;
        _logger = logger ?? NullLogger.Instance;
        _registryPath = Path.Combine(options.DataDirectory, "browser-sessions.json");
        _registry = LoadRegistry(_registryPath);
        _startedPath = Path.Combine(options.DataDirectory, "adspower-started.json");
        _started = LoadStarted(_startedPath);
    }

    public AdsPowerClient AdsPower => _adsPower;

    public ISnapshotStore? Snapshots => _options.Snapshots;

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
    /// <remarks>
    /// Профиль, открытый не Pepa (владельцем вручную), не захватывается молча: его вкладку Pepa не берёт и профиль в конце не
    /// закрывает — по <paramref name="openedElsewhere"/> шаг останавливается или работает в своей новой вкладке. Сессия
    /// записывается до запуска профиля: если запуск затянулся или Pepa упала, профиль всё равно закроется, когда процесс
    /// завершится.
    /// </remarks>
    public async Task<BrowserSession> OpenAdsPowerAsync(string processId, string name, string profileId, bool newTab, bool closeOnFinish,
        CancellationToken cancellationToken, AdsPowerOpenedElsewhere openedElsewhere = AdsPowerOpenedElsewhere.Stop)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue((processId, name), out var existing) && !existing.Closed && existing.Info.ProfileId == profileId)
            {
                return existing;
            }

            var state = await _adsPower.GetStateAsync(profileId, cancellationToken);
            if (state.Active && !IsPepaProfile(profileId))
            {
                if (openedElsewhere == AdsPowerOpenedElsewhere.Stop)
                {
                    throw new AdsPowerOpenedElsewhereException(
                        $"Профиль AdsPower {profileId} уже открыт, и открыла его не Pepa (возможно, вы вручную). Pepa не работает в чужом " +
                        "окне: закройте профиль в AdsPower и выполните шаг ещё раз — или выберите у блока «Если профиль уже открыт не Pepa» → " +
                        "«Работать в новой вкладке».");
                }

                newTab = true;
                closeOnFinish = false;
            }

            var ownTab = newTab && !closeOnFinish && state.Active && !IsPepaProfile(profileId);
            var info = new BrowserSessionInfo
            {
                ProcessId = processId, Name = name, Kind = BrowserKind.AdsPower, ProfileId = profileId,
                ResourceKey = AdsPowerResourceKey(profileId), CloseOnFinish = closeOnFinish, OwnTab = ownTab,
            };
            SaveRegistry(info);
            if (!state.Active || state.WsEndpoint is null)
            {
                MarkStarted(profileId);
                try
                {
                    state = await _adsPower.StartAsync(profileId, cancellationToken);
                }
                catch (AdsPowerException ex) when (!ex.TimedOut)
                {
                    // Точно не открылся — ни сессии, ни отметки.
                    UnmarkStarted(profileId);
                    RemoveRegistry(processId, name);
                    throw;
                }
            }

            var session = await ConnectAsync(info, state.WsEndpoint!, newTab);
            _sessions[(processId, name)] = session;
            await RememberPageAsync(session);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Запомнить вкладку процесса (на диске), чтобы после перезапуска Pepa продолжить в ней. Только профили AdsPower: обычный
    /// браузер перезапуск не переживает.
    /// </summary>
    public async Task RememberPageAsync(BrowserSession session)
    {
        if (session.Kind != BrowserKind.AdsPower || await TargetIdAsync(session.Page) is not { } targetId || targetId == session.Info.TargetId)
        {
            return;
        }

        session.Info = session.Info with { TargetId = targetId };
        SaveRegistry(session.Info);
    }

    /// <summary>Идентификатор вкладки в браузере (CDP); null — узнать не удалось.</summary>
    private static async Task<string?> TargetIdAsync(IPage page)
    {
        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            try
            {
                var info = await cdp.SendAsync("Target.getTargetInfo");
                return info is { } result && result.TryGetProperty("targetInfo", out var target) &&
                       target.TryGetProperty("targetId", out var id)
                    ? id.GetString()
                    : null;
            }
            finally
            {
                await cdp.DetachAsync();
            }
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    /// <summary>Профиль открыла Pepa: запустила сама (и ещё не закрыла) или он записан за процессом Pepa.</summary>
    private bool IsPepaProfile(string profileId)
    {
        lock (_registry)
        {
            // Своя вкладка процесса в чужом профиле не делает профиль «открытым Pepa».
            return _started.Contains(profileId) || _registry.Values.Any(i => i.ProfileId == profileId && !i.OwnTab);
        }
    }

    private void MarkStarted(string profileId)
    {
        lock (_registry)
        {
            if (_started.Add(profileId))
            {
                WriteStarted();
            }
        }
    }

    private void UnmarkStarted(string profileId)
    {
        lock (_registry)
        {
            if (_started.Remove(profileId))
            {
                WriteStarted();
            }
        }
    }

    private void WriteStarted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_startedPath)!);
        var temp = _startedPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_started.Order(StringComparer.Ordinal).ToList(), RegistryJson));
        File.Move(temp, _startedPath, overwrite: true);
    }

    private static HashSet<string> LoadStarted(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), RegistryJson) ?? [], StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый список — профили, оставленные открытыми, будут считаться открытыми не Pepa (осторожно).
        }

        return new HashSet<string>(StringComparer.Ordinal);
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

        try
        {
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
            var page = newTab ? await context.NewPageAsync() : await FindPageAsync(context, info);
            return new BrowserSession(info.ProcessId, info.Name, BrowserKind.AdsPower, context, browser, page, info);
        }
        catch
        {
            // Только отключение: браузер профиля остаётся открытым.
            await browser.CloseAsync();
            throw;
        }
    }

    /// <summary>
    /// Вкладка процесса после переподключения — запомненная. Её закрыли — шаг останавливается: другая вкладка не та страница,
    /// на которой процесс остановился, и молча продолжать в ней нельзя. Запомненная вкладка забывается, поэтому повтор шага
    /// продолжит как без неё: в последней вкладке профиля, открытого Pepa, или в новой вкладке чужого профиля (чужие вкладки
    /// Pepa не берёт).
    /// </summary>
    private async Task<IPage> FindPageAsync(IBrowserContext context, BrowserSessionInfo info)
    {
        var open = context.Pages.Where(p => !p.IsClosed).ToList();
        if (info.TargetId is { } wanted)
        {
            foreach (var candidate in open)
            {
                if (await TargetIdAsync(candidate) == wanted)
                {
                    return candidate;
                }
            }

            _logger.LogWarning("Вкладка процесса {Process} в профиле AdsPower {Profile} закрыта.", info.ProcessId, info.ProfileId);
            SaveRegistry(info with { TargetId = null });
            throw new BrowserSessionLostException(
                $"Вкладку, в которой работал процесс, в профиле AdsPower {info.ProfileId} закрыли — страница, на которой он " +
                "остановился, потеряна, и в другой вкладке Pepa сама не продолжает. «Выполнить шаг ещё раз» продолжит " +
                (info.OwnTab ? "в новой пустой вкладке." : "в последней открытой вкладке профиля."));
        }

        return info.OwnTab || open.Count == 0 ? await context.NewPageAsync() : open[^1];
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
                UnmarkStarted(profileId);
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

    public static string FirstLine(string message)
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
