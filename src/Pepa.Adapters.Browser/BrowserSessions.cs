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

        _order.AddRange(context.Pages);
        context.Page += (_, opened) =>
        {
            WatchDialogs(opened);
            lock (_order)
            {
                _order.Add(opened);
            }

            try
            {
                PageOpened?.Invoke(opened);
            }
            catch (Exception)
            {
                // Запомнить порядок вкладок не вышло — после перезапуска порядок будет тем, в каком браузер их отдаст.
            }
        };

        // Своя вкладка в чужом профиле: процессу принадлежат только она и окна, открытые с неё (вход через всплывающее окно).
        if (info.OwnTab)
        {
            _own = [];
            Own(page);
        }
    }

    private readonly HashSet<IPage>? _own;

    /// <summary>Вкладки в порядке открытия (после переподключения — в запомненном порядке, открытые без Pepa — последними).</summary>
    private readonly List<IPage> _order = [];

    /// <summary>Открылась новая вкладка — чтобы запомнить порядок вкладок на диске.</summary>
    internal event Action<IPage>? PageOpened;

    /// <summary>Порядок вкладок после переподключения (из запомненного).</summary>
    internal void Arrange(IReadOnlyList<IPage> ordered)
    {
        lock (_order)
        {
            _order.Clear();
            _order.AddRange(ordered);
        }
    }

    /// <summary>Открытые вкладки в порядке открытия.</summary>
    internal IReadOnlyList<IPage> OpenInOrder()
    {
        lock (_order)
        {
            var known = _order.Where(p => !p.IsClosed).ToList();
            known.AddRange(Context.Pages.Where(p => !p.IsClosed && !_order.Contains(p)));
            return known;
        }
    }
    private string? _lostTabNotice;

    private void Own(IPage page)
    {
        lock (_own!)
        {
            if (!_own.Add(page))
            {
                return;
            }
        }

        page.Popup += (_, popup) => Own(popup);
        try
        {
            PageOwned?.Invoke(page);
        }
        catch (Exception)
        {
            // Запомнить вкладку на диске не вышло — после перезапуска её найдут по связи «кто кого открыл».
        }
    }

    /// <summary>Вкладка стала вкладкой процесса (окно, открытое с его вкладки) — чтобы запомнить её на диске.</summary>
    internal event Action<IPage>? PageOwned;

    /// <summary>Вкладка процесса, известная по прошлому запуску Pepa.</summary>
    internal void OwnKnown(IPage page)
    {
        if (_own is not null)
        {
            Own(page);
        }
    }

    /// <summary>Вкладка — процесса: в профиле, открытом Pepa, — любая; в чужом — своя и открытые с неё.</summary>
    public bool IsOwn(IPage page)
    {
        if (_own is null)
        {
            return true;
        }

        lock (_own)
        {
            return _own.Contains(page);
        }
    }

    /// <summary>
    /// После переподключения (перезапуска Pepa) вкладки процесса в чужом профиле восстанавливаются по связям «кто кого открыл»:
    /// найдена одна запомненная вкладка, а процессу принадлежат и вкладка, открывшая её, и окна, открытые с его вкладок.
    /// </summary>
    internal async Task AdoptRelatedPagesAsync()
    {
        if (_own is null)
        {
            return;
        }

        for (var round = 0; round < 10; round++)
        {
            var changed = false;
            foreach (var candidate in Context.Pages.Where(p => !p.IsClosed).ToList())
            {
                IPage? opener;
                try
                {
                    opener = await candidate.OpenerAsync();
                }
                catch (PlaywrightException)
                {
                    continue;
                }

                if (opener is null || opener.IsClosed || IsOwn(candidate) == IsOwn(opener))
                {
                    continue;
                }

                // Своя вкладка открыта с другой — та тоже процесса; окно, открытое со своей вкладки, — тоже.
                Own(IsOwn(candidate) ? opener : candidate);
                changed = true;
            }

            if (!changed)
            {
                return;
            }
        }
    }

    /// <summary>Открытые вкладки, с которыми процесс может работать.</summary>
    /// <remarks>В порядке открытия: «Новая (открытая последней)» после перезапуска Pepa — та же, что до него.</remarks>
    public IReadOnlyList<IPage> Pages() => OpenInOrder().Where(IsOwn).ToList();

    /// <summary>Вкладку процесса закрыли, пока Pepa была выключена: пояснение для первого шага после переподключения (один раз).</summary>
    internal void NoteLostTab(string notice) => _lostTabNotice = notice;

    public string? TakeLostTabNotice() => Interlocked.Exchange(ref _lostTabNotice, null);

    public string ProcessId { get; }
    public string Name { get; }
    public BrowserKind Kind { get; }
    public IBrowserContext Context { get; }
    public IBrowser? Browser { get; }
    public IPage Page { get; internal set; }
    public BrowserSessionInfo Info { get; internal set; }

    /// <summary>Идентификаторы вкладок браузера (для запомненного порядка); меняются под блокировкой списка сессий.</summary>
    internal Dictionary<IPage, string> TabIds { get; set; } = [];

    /// <summary>Вкладка, которую Pepa запомнила как вкладку процесса (на диске).</summary>
    internal IPage? RememberedPage { get; set; }
    public bool Closed { get; private set; }

    /// <summary>
    /// Под именем сессии процесс открыл другой профиль: эта сессия больше не его «main» — её вкладки и окна не пишутся в запись
    /// нового профиля.
    /// </summary>
    internal bool Retired { get; set; }

    /// <summary>Подключение закончено (порядок вкладок восстановлен) — порядок можно писать на диск.</summary>
    internal bool OrderReady { get; set; }

    /// <summary>Окна отвечает человек (запись действий): Pepa их только видит.</summary>
    internal bool ManualDialogs { get; set; }

    private void WatchDialogs(IPage page) => page.Dialog += (_, dialog) => _ = AnswerAsync(page, dialog);

    private async Task AnswerAsync(IPage page, IDialog dialog)
    {
        // Окна во вкладках владельца (в его профиле) — его: Pepa на них не отвечает и не считает их окнами процесса. Обработчик
        // при этом есть, поэтому браузер не закрывает окно сам — владелец ответит на него в своей вкладке.
        if (ManualDialogs || !IsOwn(page))
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

        Page = Pages().LastOrDefault()
               ?? throw new BrowserSessionLostException(_own is null
                   ? $"Все вкладки браузера «{Name}» закрыты."
                   : $"Вкладки процесса в профиле AdsPower {Info.ProfileId} закрыты, а в других вкладках профиля (их открыл не процесс) " +
                     "Pepa не работает.");
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

    /// <summary>
    /// Все вкладки процесса в чужом профиле (своя и окна, открытые с неё), идентификаторами вкладок браузера: после перезапуска
    /// Pepa процесс узнаёт их все, а не только ту, в которой остановился.
    /// </summary>
    public IReadOnlyList<string>? OwnedTargets { get; init; }

    /// <summary>Открытые вкладки профиля в порядке открытия (идентификаторы): после перезапуска «новая вкладка» — та же.</summary>
    public IReadOnlyList<string>? TabOrder { get; init; }

    /// <summary>
    /// Прежний браузер под служебным именем «… (прежний N)»: процесс открыл под его именем другой профиль. Ждёт конца процесса
    /// (или нового открытия того же профиля, или его закрытия под другим именем).
    /// </summary>
    public bool Retired { get; init; }

    /// <summary>
    /// Какой запуск браузера профиля подключён (адрес отладки AdsPower: у каждого запуска — свой идентификатор браузера). Профиль
    /// закрыли (перезагрузка, AdsPower) и открыли заново не Pepa — адрес другой: процесс в этом окне не работает и его не
    /// закрывает. null — ещё не подключались (например, итог запуска неизвестен).
    /// </summary>
    public string? Endpoint { get; init; }
}

/// <summary>Чем закончилось закрытие: запись сессии и что сделано с профилем AdsPower.</summary>
/// <param name="Note">Профиль не закрыт, хотя просили (чужой, у другого процесса, открыт заново не Pepa), — почему.</param>
public sealed record BrowserClosed(BrowserSessionInfo Info, bool ProfileStopped, string? Note);

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
    /// <summary>Журнал Pepa (задаётся после запуска приложения; до этого — никуда).</summary>
    public ILogger Logger { get; set; }
    private readonly ConcurrentDictionary<(string, string), BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _registryPath;
    private Dictionary<string, BrowserSessionInfo> _registry;

    /// <summary>Профили AdsPower, которые запустила Pepa и ещё не закрыла (на диске: переживают перезапуск Pepa).</summary>
    private readonly string _startedPath;
    /// <summary>
    /// Профили, которые открыла Pepa (и ещё не закрыла): профиль → какой запуск браузера (адрес отладки); null — запуск отправлен,
    /// итог неизвестен.
    /// </summary>
    private readonly Dictionary<string, string?> _started;
    private IPlaywright? _playwright;

    public BrowserSessions(BrowserModuleOptions options, AdsPowerClient adsPower, ILogger? logger = null)
    {
        _options = options;
        _adsPower = adsPower;
        Logger = logger ?? NullLogger.Instance;
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
            // Под этим именем открыт профиль AdsPower — это не «уже открытый браузер»: иначе «Открыть браузер» молча работал
            // бы в аккаунте профиля. Профиль переходит под служебное имя (ниже), браузер открывается.
            if (_sessions.TryGetValue((processId, name), out var existing) && !existing.Closed && existing.Kind == BrowserKind.Local)
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
            // Под этим именем был записан профиль AdsPower (например, до перезапуска Pepa): он закроется в конце процесса, как
            // задано при открытии, а не останется открытым навсегда.
            await RetireAsync(processId, name, profileId: null, existing);
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
            if (state.Active && !IsPepaProfile(profileId, state.WsEndpoint))
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

            var ownTab = newTab && !closeOnFinish && state.Active && !IsPepaProfile(profileId, state.WsEndpoint);
            var info = new BrowserSessionInfo
            {
                ProcessId = processId, Name = name, Kind = BrowserKind.AdsPower, ProfileId = profileId,
                ResourceKey = AdsPowerResourceKey(profileId), CloseOnFinish = closeOnFinish, OwnTab = ownTab,
            };

            // Под этим именем у процесса другой браузер (другой профиль, без «Закрыть браузер» между ними): он не теряется.
            var retired = await RetireAsync(processId, name, profileId, existing);

            // Что было до этого вызова: отметки прежней попытки (с неизвестным итогом) этот вызов не снимает.
            var hadRecord = FindInfo(processId, name) is not null;
            bool hadMark;
            string? previousMark;
            lock (_registry)
            {
                hadMark = _started.TryGetValue(profileId, out previousMark);
            }

            SaveRegistry(info);
            if (!state.Active || state.WsEndpoint is null)
            {
                MarkStarted(profileId);
                try
                {
                    state = await _adsPower.StartAsync(profileId, cancellationToken);
                }
                catch (AdsPowerException ex) when (!ex.TimedOut && !ex.MayBeStarting)
                {
                    // Точно не открылся этим запросом — снять то, что отметил он сам.
                    RestoreMark(profileId, hadMark, previousMark);

                    if (!hadRecord)
                    {
                        RemoveRegistry(processId, name);
                        if (retired is not null)
                        {
                            Unretire(processId, name, retired);
                        }
                    }

                    throw;
                }
            }

            var session = await ConnectAsync(info, state.WsEndpoint!, newTab);
            _sessions[(processId, name)] = session;
            DropRetired(processId, profileId);
            // Какой запуск браузера подключён: открытый заново не Pepa (после перезагрузки) процесс потом не примет за свой.
            RememberConnected(session, state.WsEndpoint);
            RememberLaunch(profileId, state.WsEndpoint);
            await RememberPageAsync(session);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Процесс открывает под именем <paramref name="name"/> другой профиль, а прежний браузер под этим именем ещё записан
    /// (или подключён). Прежний не закрывается сейчас (это было бы действием, которого никто не просил), а переходит под
    /// служебное имя до конца процесса: тогда он закроется, если так было задано при открытии (а не останется открытым
    /// навсегда), и его вкладки больше не пишутся в запись нового профиля — иначе после перезапуска Pepa процесс продолжил бы
    /// в чужом аккаунте. Профиль AdsPower при этом только отключается от Playwright: окна его сайтов Pepa больше не отвечает.
    /// </summary>
    /// <returns>Служебное имя, под которым теперь запись прежнего браузера; null — переносить было нечего.</returns>
    private async Task<string?> RetireAsync(string processId, string name, string? profileId, BrowserSession? live)
    {
        BrowserSessionInfo? previous;
        string? retiredName = null;
        lock (_registry)
        {
            previous = _registry.GetValueOrDefault(Key(processId, name));
            if (previous is not null && previous.ProfileId != profileId)
            {
                for (var n = 1; ; n++)
                {
                    var candidate = $"{name} (прежний {n})";
                    if (!_registry.ContainsKey(Key(processId, candidate)) && !_sessions.ContainsKey((processId, candidate)))
                    {
                        retiredName = candidate;
                        break;
                    }
                }

                _registry.Remove(Key(processId, name));
                _registry[Key(processId, retiredName)] = previous with { Name = retiredName, Retired = true };
                WriteRegistry();
            }
        }

        if (live is null || live.Info.ProfileId == profileId)
        {
            return retiredName;
        }

        live.Retired = true;
        _sessions.TryRemove((processId, name), out _);
        if (retiredName is not null)
        {
            _sessions[(processId, retiredName)] = live;
        }

        if (live.Kind == BrowserKind.AdsPower)
        {
            await CloseQuietlyAsync(live);
        }

        Logger.LogWarning("Процесс {Process} открыл под именем «{Name}» другой профиль; прежний браузер — «{Retired}» до конца процесса.",
            processId, name, retiredName);
        return retiredName;
    }

    /// <summary>Новый профиль точно не открылся — прежний браузер снова под своим именем (подключится при следующем шаге).</summary>
    private void Unretire(string processId, string name, string retiredName)
    {
        lock (_registry)
        {
            if (_registry.Remove(Key(processId, retiredName), out var previous))
            {
                _registry[Key(processId, name)] = previous with { Name = name, Retired = false };
                WriteRegistry();
            }
        }

        if (_sessions.TryRemove((processId, retiredName), out var live))
        {
            live.Retired = false;
            // Обычный браузер при переносе не отключался — он снова под своим именем. Отключённый профиль AdsPower
            // подключится заново по записи при следующем шаге.
            if (!live.Closed)
            {
                _sessions[(processId, name)] = live;
            }
        }
    }

    /// <summary>
    /// Профиль снова открыт (или закрыт) процессом под своим именем — его прежние записи «… (прежний N)» больше не нужны: иначе
    /// в конце процесса они закрыли бы профиль ещё раз — вопреки последней настройке или уже у другого процесса.
    /// </summary>
    private void DropRetired(string processId, string profileId)
    {
        lock (_registry)
        {
            var stale = _registry.Values.Where(i => i.ProcessId == processId && i.Retired && i.ProfileId == profileId)
                .Select(i => i.Name).ToList();
            foreach (var retiredName in stale)
            {
                _registry.Remove(Key(processId, retiredName));
                _sessions.TryRemove((processId, retiredName), out _);
            }

            if (stale.Count > 0)
            {
                WriteRegistry();
            }
        }
    }

    /// <summary>
    /// Запись на диске — этой сессии: не заменена другим профилем под тем же именем. Вызывать под блокировкой списка.
    /// </summary>
    private bool OwnsRecord(BrowserSession session) =>
        !session.Retired && _registry.TryGetValue(Key(session.ProcessId, session.Name), out var record) &&
        record.Kind == session.Kind && record.ProfileId == session.Info.ProfileId;

    private void RememberConnected(BrowserSession session, string? endpoint)
    {
        lock (_registry)
        {
            if (endpoint is null || session.Info.Endpoint == endpoint)
            {
                return;
            }

            session.Info = session.Info with { Endpoint = endpoint };
            if (OwnsRecord(session))
            {
                SaveRegistry(session.Info);
            }
        }
    }

    /// <summary>
    /// Запомнить вкладку процесса (на диске), чтобы после перезапуска Pepa продолжить в ней. Только профили AdsPower: обычный
    /// браузер перезапуск не переживает.
    /// </summary>
    public async Task RememberPageAsync(BrowserSession session)
    {
        var page = session.Page;
        // Чужую вкладку (в профиле владельца) Pepa своей не запоминает.
        if (session.Kind != BrowserKind.AdsPower || page == session.RememberedPage || !session.IsOwn(page))
        {
            return;
        }

        if (await TargetIdAsync(page) is not { } targetId)
        {
            return;
        }

        session.RememberedPage = page;
        if (targetId == session.Info.TargetId)
        {
            return;
        }

        lock (_registry)
        {
            session.Info = session.Info with { TargetId = targetId };
            // Сессию уже закрыли (запись удалена) или заменили другим профилем — не возвращать и не переписывать.
            if (OwnsRecord(session))
            {
                SaveRegistry(session.Info);
            }
        }
    }

    /// <summary>Новая вкладка — в конец запомненного порядка.</summary>
    private async Task RecordTabOrderAsync(BrowserSession session, IPage page)
    {
        if (await TargetIdAsync(page) is not { } id)
        {
            return;
        }

        lock (_registry)
        {
            session.TabIds[page] = id;
        }

        // Пока идёт подключение, порядок ещё не восстановлен — его запишет само подключение.
        if (session.OrderReady)
        {
            SaveTabOrder(session);
        }
    }

    /// <summary>Порядок открытых вкладок — на диск (закрытые выпадают).</summary>
    private void SaveTabOrder(BrowserSession session)
    {
        lock (_registry)
        {
            var order = session.OpenInOrder().Select(p => session.TabIds.GetValueOrDefault(p)).OfType<string>().ToList();
            if (!OwnsRecord(session) || (session.Info.TabOrder is { } saved && saved.SequenceEqual(order)))
            {
                return;
            }

            session.Info = session.Info with { TabOrder = order };
            SaveRegistry(session.Info);
        }
    }

    /// <summary>Запомнить на диске ещё одну вкладку процесса в чужом профиле (окно, открытое с его вкладки).</summary>
    private async Task RecordOwnedAsync(BrowserSession session, IPage page)
    {
        if (await TargetIdAsync(page) is not { } targetId)
        {
            return;
        }

        lock (_registry)
        {
            if (session.Info.OwnedTargets?.Contains(targetId) == true || !OwnsRecord(session))
            {
                return;
            }

            session.Info = session.Info with { OwnedTargets = [.. session.Info.OwnedTargets ?? [], targetId] };
            SaveRegistry(session.Info);
        }
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

    /// <summary>
    /// Запуск браузера, а не адрес целиком: идентификатор браузера в адресе отладки (…/devtools/browser/&lt;id&gt;) свой у каждого
    /// запуска; порт и имя узла AdsPower может отдавать по-разному.
    /// </summary>
    internal static string? LaunchOf(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        var at = endpoint.LastIndexOf("/devtools/browser/", StringComparison.Ordinal);
        return at >= 0 ? endpoint[(at + "/devtools/browser/".Length)..] : endpoint;
    }

    private static bool SameLaunch(string? recorded, string? active) =>
        recorded is null || active is null || LaunchOf(recorded) == LaunchOf(active);

    /// <summary>
    /// Профиль открыла Pepa: запустила сама (и ещё не закрыла) или он записан за процессом Pepa — и это тот же запуск браузера,
    /// что открыт сейчас. Профиль закрыли (перезагрузка, AdsPower) и владелец открыл его заново — уже не Pepa: отметка снимается.
    /// </summary>
    private bool IsPepaProfile(string profileId, string? activeEndpoint)
    {
        lock (_registry)
        {
            if (_started.TryGetValue(profileId, out var launched))
            {
                if (SameLaunch(launched, activeEndpoint))
                {
                    return true;
                }

                _started.Remove(profileId);
                WriteStarted();
                Logger.LogWarning("Профиль AdsPower {Profile} открыт заново не Pepa (другой запуск браузера) — отметка «открыла Pepa» снята.", profileId);
            }

            // Своя вкладка процесса в чужом профиле не делает профиль «открытым Pepa».
            return _registry.Values.Any(i => i.ProfileId == profileId && !i.OwnTab && SameLaunch(i.Endpoint, activeEndpoint));
        }
    }

    /// <summary>Запуск отправлен (итог неизвестен, пока браузер не подключён).</summary>
    private void MarkStarted(string profileId)
    {
        lock (_registry)
        {
            _started[profileId] = null;
            WriteStarted();
        }
    }

    /// <summary>Какой запуск браузера Pepa подключила — для профиля, который она открыла.</summary>
    private void RememberLaunch(string profileId, string? endpoint)
    {
        lock (_registry)
        {
            if (_started.TryGetValue(profileId, out var known) && known != endpoint)
            {
                _started[profileId] = endpoint;
                WriteStarted();
            }
        }
    }

    private void RestoreMark(string profileId, bool had, string? previous)
    {
        lock (_registry)
        {
            if (had)
            {
                _started[profileId] = previous;
            }
            else
            {
                _started.Remove(profileId);
            }

            WriteStarted();
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
        File.WriteAllText(temp, JsonSerializer.Serialize(new SortedDictionary<string, string?>(_started, StringComparer.Ordinal), RegistryJson));
        File.Move(temp, _startedPath, overwrite: true);
    }

    private static Dictionary<string, string?> LoadStarted(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                // Прежний вид — список профилей без запуска браузера: считаем «итог неизвестен» (как раньше — профиль Pepa).
                return text.TrimStart().StartsWith('[')
                    ? (JsonSerializer.Deserialize<List<string>>(text, RegistryJson) ?? []).Distinct(StringComparer.Ordinal)
                        .ToDictionary(id => id, _ => (string?)null, StringComparer.Ordinal)
                    : new Dictionary<string, string?>(JsonSerializer.Deserialize<Dictionary<string, string?>>(text, RegistryJson) ?? [],
                        StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый список — профили, оставленные открытыми, будут считаться открытыми не Pepa (осторожно).
        }

        return new Dictionary<string, string?>(StringComparer.Ordinal);
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
            // Закрытую вкладку процесса заменила новая — сессия сразу знает её: иначе следующая запись на диск вернула бы
            // туда закрытую, и каждый перезапуск Pepa открывал бы ещё одну пустую вкладку.
            var (page, lost, found) = newTab ? (await context.NewPageAsync(), null, info) : await FindPageAsync(context, info);
            info = found;
            var session = new BrowserSession(info.ProcessId, info.Name, BrowserKind.AdsPower, context, browser, page, info);
            if (lost is not null)
            {
                session.NoteLostTab(lost);
            }

            // Вкладки, открывшиеся, пока идёт подключение (стартовые страницы, расширения), тоже получают идентификатор:
            // подписка — до чтения идентификаторов, иначе такая вкладка не попала бы в запомненный порядок.
            var ids = session.TabIds;
            session.PageOpened += opened => _ = RecordTabOrderAsync(session, opened);

            // Идентификаторы открытых вкладок: по ним — запомненный порядок и вкладки процесса до перезапуска.
            foreach (var open in context.Pages.Where(p => !p.IsClosed).ToList())
            {
                if (await TargetIdAsync(open) is { } id)
                {
                    lock (_registry)
                    {
                        ids[open] = id;
                    }
                }
            }

            if (!newTab && info.TabOrder is { Count: > 0 } order)
            {
                // Вкладки, открытые, пока Pepa была выключена, — новее всех запомненных (порядок среди них — как отдал браузер).
                var position = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var at = 0; at < order.Count; at++)
                {
                    position.TryAdd(order[at], at);
                }

                Dictionary<IPage, string> known;
                lock (_registry)
                {
                    known = new Dictionary<IPage, string>(ids);
                }

                session.Arrange([.. context.Pages.Where(p => !p.IsClosed)
                    .OrderBy(p => known.TryGetValue(p, out var id) && position.TryGetValue(id, out var at) ? at : int.MaxValue)]);
            }

            // Вкладки, ещё без идентификатора (открылись во время подключения), — до записи порядка.
            foreach (var open in session.OpenInOrder())
            {
                bool missing;
                lock (_registry)
                {
                    missing = !ids.ContainsKey(open);
                }

                if (missing && await TargetIdAsync(open) is { } id)
                {
                    lock (_registry)
                    {
                        ids[open] = id;
                    }
                }
            }

            session.OrderReady = true;
            SaveTabOrder(session);

            if (info.OwnTab)
            {
                // Вкладки процесса, запомненные до перезапуска, — снова его; а также связанные с ними «кто кого открыл».
                if (!newTab && info.OwnedTargets is { Count: > 0 } ownedBefore)
                {
                    List<(IPage Page, string Id)> current;
                    lock (_registry)
                    {
                        current = ids.Select(e => (e.Key, e.Value)).ToList();
                    }

                    foreach (var (candidate, id) in current)
                    {
                        if (ownedBefore.Contains(id))
                        {
                            session.OwnKnown(candidate);
                        }
                    }
                }

                if (!newTab)
                {
                    await session.AdoptRelatedPagesAsync();
                }

                session.PageOwned += page => _ = RecordOwnedAsync(session, page);
                foreach (var owned in session.Pages())
                {
                    await RecordOwnedAsync(session, owned);
                }
            }

            return session;
        }
        catch
        {
            // Только отключение: браузер профиля остаётся открытым.
            await browser.CloseAsync();
            throw;
        }
    }

    /// <summary>
    /// Вкладка процесса после переподключения — запомненная. Её закрыли — другая вкладка не та страница, на которой процесс
    /// остановился, и молча продолжать в ней нельзя: Pepa открывает вместо неё новую пустую вкладку (она теперь вкладка
    /// процесса) и возвращает пояснение — первый шаг после переподключения не выполняется, а сообщает его. Дальше процесс
    /// работает только в новой вкладке: человек может открыть в ней нужную страницу и повторить шаг.
    /// </summary>
    /// <returns>Вкладка, пояснение о потерянной вкладке и запись сессии — с новой вкладкой, если старую закрыли.</returns>
    private async Task<(IPage Page, string? Lost, BrowserSessionInfo Info)> FindPageAsync(IBrowserContext context, BrowserSessionInfo info)
    {
        var open = context.Pages.Where(p => !p.IsClosed).ToList();
        if (info.TargetId is { } wanted)
        {
            foreach (var candidate in open)
            {
                if (await TargetIdAsync(candidate) == wanted)
                {
                    return (candidate, null, info);
                }
            }

            Logger.LogWarning("Вкладка процесса {Process} в профиле AdsPower {Profile} закрыта.", info.ProcessId, info.ProfileId);
            var replacement = await context.NewPageAsync();
            if (await TargetIdAsync(replacement) is { } replacementId)
            {
                info = info with { TargetId = replacementId };
                SaveRegistry(info);
            }

            return (replacement,
                $"Вкладку, в которой работал процесс, в профиле AdsPower {info.ProfileId} закрыли — страница, на которой он " +
                "остановился, потеряна, и в другой вкладке Pepa не продолжает. Pepa открыла вместо неё новую пустую вкладку: " +
                "откройте в ней нужную страницу и выберите «Выполнить шаг ещё раз» — или остановите процесс.", info);
        }

        return (info.OwnTab || open.Count == 0 ? await context.NewPageAsync() : open[^1], null, info);
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

            if (!SameLaunch(info.Endpoint, state.WsEndpoint))
            {
                // Профиль закрывался (перезагрузка, AdsPower) и открыт заново не Pepa: это не тот браузер, где работал процесс.
                // В нём процесс не работает и в конце его не закрывает.
                lock (_registry)
                {
                    if (_registry.ContainsKey(Key(processId, name)))
                    {
                        SaveRegistry(info with { CloseOnFinish = false, Endpoint = null, OwnTab = true });
                    }

                    if (_started.TryGetValue(info.ProfileId!, out var launched) && !SameLaunch(launched, state.WsEndpoint))
                    {
                        _started.Remove(info.ProfileId!);
                        WriteStarted();
                    }
                }

                throw new BrowserSessionLostException(
                    $"Профиль AdsPower {info.ProfileId} закрывался (перезагрузка компьютера или AdsPower) и открыт заново не Pepa — " +
                    "страница процесса потеряна. В этом окне Pepa не работает и закрывать его не будет. Откройте профиль снова блоком " +
                    "«Открыть профиль AdsPower» (если окно открыли вы — закройте его или выберите «Работать в новой вкладке»).");
            }

            session = await ConnectAsync(info, state.WsEndpoint, newTab: false);
            _sessions[(processId, name)] = session;
            RememberConnected(session, state.WsEndpoint);
            Logger.LogInformation("Переподключение к профилю AdsPower {Profile} для процесса {Process}.", info.ProfileId, processId);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Есть ли у процесса живая сессия с таким именем (без переподключения); <paramref name="kind"/> — только такого вида.</summary>
    public bool IsOpen(string processId, string name, BrowserKind? kind = null) =>
        _sessions.TryGetValue((processId, name), out var session) && !session.Closed && (kind is null || session.Kind == kind);

    public BrowserSessionInfo? FindInfo(string processId, string name) => _registry.GetValueOrDefault(Key(processId, name));

    // ───────────── Закрытие ─────────────

    /// <summary>Закрыть сессию: обычный браузер закрывается; профиль AdsPower — по желанию (иначе только отключение).</summary>
    public async Task<BrowserClosed?> CloseAsync(string processId, string name, bool stopProfile, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await CloseCoreAsync(processId, name, stopProfile, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Закрыть сессию; вызывать под <see cref="_lock"/>.</summary>
    private async Task<BrowserClosed?> CloseCoreAsync(string processId, string name, bool stopProfile, CancellationToken cancellationToken)
    {
        _sessions.TryRemove((processId, name), out var session);
        var info = session?.Info ?? _registry.GetValueOrDefault(Key(processId, name));
        if (session is not null)
        {
            if (info is { OwnTab: true } && !session.Closed)
            {
                // Своя вкладка в чужом профиле: закрываются только вкладки процесса, окно владельца остаётся.
                foreach (var page in session.Pages())
                {
                    try
                    {
                        await page.CloseAsync();
                    }
                    catch (PlaywrightException)
                    {
                        // Уже закрыта.
                    }
                }
            }

            await CloseQuietlyAsync(session);
        }

        var stopped = false;
        string? note = null;
        if (info is { Kind: BrowserKind.AdsPower, ProfileId: { } profileId } && stopProfile)
        {
            bool usedByOther;
            lock (_registry)
            {
                usedByOther = _registry.Values.Any(i => i.ProfileId == profileId && i.ProcessId != processId);
            }

            if (info.OwnTab)
            {
                // Профиль открыт не Pepa (вкладка процесса — в окне владельца): его Pepa не закрывает ни при каких условиях.
                note = $"Профиль AdsPower {profileId} открыт не Pepa — закрыты только вкладки процесса, профиль оставлен открытым.";
            }
            else if (usedByOther)
            {
                // Процесс отпустил профиль раньше («Освободить ресурс»), и он уже у другого процесса: закрыть его значило бы
                // оборвать чужую работу — один профиль работает на один процесс.
                note = $"Профиль AdsPower {profileId} уже у другого процесса — Pepa его не закрывает.";
            }
            else if (info.Endpoint is { } launched &&
                     await _adsPower.GetStateAsync(profileId, cancellationToken) is var state &&
                     (!state.Active || !SameLaunch(launched, state.WsEndpoint)))
            {
                // Тот запуск браузера уже закрыт: профиль закрыт — или открыт заново не Pepa (тогда это окно владельца).
                UnmarkStarted(profileId);
                note = state.Active
                    ? $"Профиль AdsPower {profileId} закрывался и открыт заново не Pepa — это не окно процесса, Pepa его не закрывает."
                    : null;
                stopped = !state.Active;
            }
            else
            {
                await _adsPower.StopAsync(profileId, cancellationToken);
                UnmarkStarted(profileId);
                stopped = true;
            }

            if (note is not null)
            {
                Logger.LogWarning("{Note} Процесс {Process}.", note, processId);
            }
        }

        RemoveRegistry(processId, name);
        if (info is { Kind: BrowserKind.AdsPower, ProfileId: { } closed, Retired: false })
        {
            // Профиль закрыт (или оставлен открытым) под своим именем — прежняя запись о нём не закроет его ещё раз в конце.
            DropRetired(processId, closed);
        }

        return info is null ? null : new BrowserClosed(info, stopped, note);
    }

    /// <summary>Процесс завершился: закрыть его браузеры (профили AdsPower — если так настроено при открытии).</summary>
    /// <remarks>
    /// Под одной блокировкой от начала до конца, и блокировка берётся сразу при вызове (из события завершения процесса — раньше,
    /// чем движок запустит процесс, получивший профили этого): иначе тот успел бы подключиться к профилю между закрытием двух
    /// браузеров, а закрытие следующего закрыло бы профиль под ним.
    /// </remarks>
    public Task ReleaseProcessAsync(string processId)
    {
        var release = ReleaseProcessCoreAsync(processId);
        // Закрытие идёт после завершения процесса, в фоне: остановка Pepa его дожидается (DisposeAsync), иначе профиль
        // остался бы открытым, а запись о нём — забыта при следующем запуске.
        _releases[release] = true;
        _ = release.ContinueWith(done => _releases.TryRemove(done, out _), TaskScheduler.Default);
        return release;
    }

    private readonly ConcurrentDictionary<Task, bool> _releases = new();

    /// <summary>
    /// Закрыть профиль в конце процесса не удалось (или не удалось бы: Pepa закрылась раньше) — что сказать в журнале процесса.
    /// </summary>
    public event Action<string, string>? ReleaseProblem;

    private void ReportReleaseProblem(string processId, string message)
    {
        try
        {
            ReleaseProblem?.Invoke(processId, message);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Не удалось записать в журнал процесса {Process}: {Message}", processId, message);
        }
    }

    private async Task ReleaseProcessCoreAsync(string processId)
    {
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            List<(string Name, bool CloseOnFinish)> names;
            lock (_registry)
            {
                names = _registry.Values.Where(i => i.ProcessId == processId).Select(i => (i.Name, i.CloseOnFinish))
                    .Concat(_sessions.Keys.Where(k => k.Item1 == processId).Select(k => (k.Item2, true)))
                    .DistinctBy(n => n.Item1)
                    .ToList();
            }

            foreach (var (name, closeOnFinish) in names)
            {
                try
                {
                    var info = FindInfo(processId, name);
                    // Прежнюю запись могло снять закрытие профиля под другим именем выше по списку.
                    if (info is null && !_sessions.ContainsKey((processId, name)))
                    {
                        continue;
                    }

                    var closed = await CloseCoreAsync(processId, name, stopProfile: info?.CloseOnFinish ?? closeOnFinish, CancellationToken.None);
                    if (closed?.Note is { } note)
                    {
                        ReportReleaseProblem(processId, note);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Не удалось закрыть браузер «{Name}» процесса {Process}.", name, processId);
                    var profile = FindInfo(processId, name)?.ProfileId;
                    // Процесс завершён: его запись больше не нужна и не должна считаться «профиль у другого процесса» — иначе
                    // следующий процесс с этим профилем его не закрыл бы. Отметка «открыла Pepa» остаётся: следующий закроет.
                    RemoveRegistry(processId, name);
                    ReportReleaseProblem(processId, profile is null
                        ? $"Не удалось закрыть браузер «{name}»: {ex.Message}"
                        : $"Не удалось закрыть профиль AdsPower {profile} в конце процесса ({ex.Message}) — он мог остаться открытым. " +
                          "Закройте его в AdsPower, если он не нужен.");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// При запуске Pepa: забыть сессии процессов, которые уже не активны. Внешних действий не выполняется (после сбоя ничего
    /// не делается само) — но о профилях AdsPower, которые Pepa должна была закрыть в конце процесса и не успела (закрылась
    /// раньше), сообщается в журнале процесса.
    /// </summary>
    public void Prune(IReadOnlyCollection<string> activeProcessIds)
    {
        var active = activeProcessIds.ToHashSet(StringComparer.Ordinal);
        List<BrowserSessionInfo> stale;
        lock (_registry)
        {
            stale = _registry.Values.Where(i => !active.Contains(i.ProcessId)).ToList();
            foreach (var info in stale)
            {
                _registry.Remove(Key(info.ProcessId, info.Name));
            }

            if (stale.Count > 0)
            {
                WriteRegistry();
            }
        }

        foreach (var info in stale.Where(i => i is { Kind: BrowserKind.AdsPower, CloseOnFinish: true, OwnTab: false, ProfileId: not null }))
        {
            ReportReleaseProblem(info.ProcessId,
                $"Pepa закрылась раньше, чем закрыла профиль AdsPower {info.ProfileId} в конце процесса, — он мог остаться открытым. " +
                "Закройте его в AdsPower, если он не нужен (следующий процесс с этим профилем закроет его сам).");
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
        // Закрытие браузеров завершившихся процессов — до конца (в разумный срок): иначе профиль остался бы открытым.
        try
        {
            await Task.WhenAll(_releases.Keys).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Закрытие браузеров завершившихся процессов не закончилось до остановки Pepa.");
        }

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
