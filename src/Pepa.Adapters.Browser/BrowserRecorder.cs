using System.Text.Json;
using Microsoft.Playwright;

namespace Pepa.Adapters.Browser;

/// <summary>Что записано: переход, нажатие, ввод, выбор, отметка, клавиша, новая вкладка.</summary>
public sealed record RecordedAction
{
    /// <summary>goto, click, fill, select, check, uncheck, press, tab.</summary>
    public required string Kind { get; init; }
    public string? Selector { get; init; }
    public string? Value { get; init; }
    public string? Url { get; init; }

    /// <summary>Пояснение для пользователя: «пароль не записан», «элемент найден по положению — проверьте» и т.п.</summary>
    public string? Note { get; init; }

    /// <summary>Подпись элемента (текст кнопки, поле) — для названия блока.</summary>
    public string? Label { get; init; }

    /// <summary>Поле пароля: значение не записано.</summary>
    public bool Secret { get; init; }
}

public enum RecorderBrowser
{
    /// <summary>Браузер Pepa с сохранённым входом (профиль «main»).</summary>
    Pepa,
    AdsPower,
}

public sealed record RecorderStart(RecorderBrowser Browser, string? ProfileId, string? Url, string Channel = "msedge", bool Headless = false);

public sealed record RecorderState(bool Recording, RecorderBrowser? Browser, string? ProfileId, string? StartUrl, IReadOnlyList<RecordedAction> Actions);

/// <summary>
/// Запись действий пользователя в браузере: Pepa открывает браузер (свой профиль или профиль AdsPower), следит за
/// нажатиями и вводом на страницах и превращает их в шаги с селекторами. Записывается только главная рамка страницы;
/// пароли не записываются.
/// </summary>
public sealed class BrowserRecorder(BrowserSessions sessions) : IAsyncDisposable
{
    /// <summary>«Процесс» записи в списке сессий браузера (не настоящий процесс движка).</summary>
    public const string SessionOwner = "pepa-recorder";

    private const string SessionName = "main";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<RecordedAction> _actions = [];
    private readonly object _gate = new();
    private BrowserSession? _session;
    private RecorderStart? _start;
    private DateTime _lastUserAction = DateTime.MinValue;

    /// <summary>Записано новое действие или запись началась/закончилась.</summary>
    public event Action? Changed;

    public RecorderState State
    {
        get
        {
            lock (_gate)
            {
                return new RecorderState(_session is { Closed: false }, _start?.Browser, _start?.ProfileId, _start?.Url, _actions.ToList());
            }
        }
    }

    public async Task StartAsync(RecorderStart start, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_session is { Closed: false })
            {
                throw new RecorderException("Запись уже идёт. Остановите её, прежде чем начать новую.");
            }

            lock (_gate)
            {
                _actions.Clear();
            }

            BrowserSession session;
            try
            {
                session = start.Browser == RecorderBrowser.AdsPower
                    ? await sessions.OpenAdsPowerAsync(SessionOwner, SessionName,
                        string.IsNullOrWhiteSpace(start.ProfileId) ? throw new RecorderException("Укажите профиль AdsPower.") : start.ProfileId.Trim(),
                        newTab: true, closeOnFinish: false, cancellationToken)
                    : await sessions.OpenLocalAsync(SessionOwner, SessionName, new LocalBrowserOptions
                    {
                        Channel = start.Channel,
                        Headless = start.Headless,
                        UserDataDir = Path.Combine(sessions.ProfilesDirectory, OpenBrowserNode.SafeName("main")),
                    }, resourceKey: null, closeOnFinish: true, cancellationToken);
            }
            catch (BrowserStartException ex)
            {
                throw new RecorderException(ex.Message);
            }
            catch (AdsPowerException ex)
            {
                throw new RecorderException(ex.Message);
            }

            await session.Context.ExposeBindingAsync("__pepaRecord", (BindingSource source, string json) => OnRecorded(source, json));
            await session.Context.AddInitScriptAsync(RecorderScript.Source);
            session.Context.Page += (_, page) => OnNewPage(page);
            foreach (var page in session.Context.Pages)
            {
                Watch(page);
                await TryInjectAsync(page);
            }

            _session = session;
            _start = start;
            if (start.Url is { Length: > 0 } url)
            {
                Add(new RecordedAction { Kind = "goto", Url = url.Trim() });
                await session.CurrentPage().GotoAsync(url.Trim(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            }
        }
        finally
        {
            _lock.Release();
        }

        Raise();
    }

    /// <summary>Остановить запись: браузер Pepa закрывается, профиль AdsPower остаётся открытым.</summary>
    public async Task<IReadOnlyList<RecordedAction>> StopAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
            {
                // Последний ввод (поле, из которого не ушли) — забрать до закрытия.
                foreach (var page in _session.Context.Pages.Where(p => !p.IsClosed))
                {
                    try
                    {
                        await page.EvaluateAsync("() => window.__pepaFlush && window.__pepaFlush()");
                    }
                    catch (PlaywrightException)
                    {
                    }
                }

                await Task.Delay(150, cancellationToken);
                await sessions.CloseAsync(SessionOwner, SessionName, stopProfile: false, cancellationToken);
                _session = null;
            }
        }
        finally
        {
            _lock.Release();
        }

        Raise();
        return State.Actions;
    }

    private void OnNewPage(IPage page)
    {
        Watch(page);
        Add(new RecordedAction { Kind = "tab", Note = "открылась новая вкладка — дальше блоки работают в ней" });
    }

    private void Watch(IPage page) => page.FrameNavigated += (_, frame) =>
    {
        if (frame != page.MainFrame || frame.Url is "about:blank" or "" || frame.Url.StartsWith("chrome", StringComparison.Ordinal))
        {
            return;
        }

        _ = RecordNavigationAsync(page, frame.Url);
    };

    /// <summary>
    /// Переход записывается, только если адрес ввёл сам пользователь (или открыл закладку): переходы по ссылкам, отправка
    /// форм и перенаправления сайта — следствие уже записанных нажатий, а их адреса бывают одноразовыми.
    /// </summary>
    private async Task RecordNavigationAsync(IPage page, string url)
    {
        bool typed;
        try
        {
            typed = await TransitionTypeAsync(page) is "typed" or "address_bar" or "auto_bookmark" or "generated" or "keyword";
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or KeyNotFoundException)
        {
            // Браузер не сообщил тип перехода — запасной признак: давно не было нажатий.
            typed = DateTime.UtcNow - _lastUserAction > TimeSpan.FromSeconds(2);
        }

        if (typed && LastUrl() != url)
        {
            Add(new RecordedAction { Kind = "goto", Url = url });
        }
    }

    private static async Task<string?> TransitionTypeAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        try
        {
            var history = await cdp.SendAsync("Page.getNavigationHistory");
            if (history is not { } root)
            {
                return null;
            }

            var index = root.GetProperty("currentIndex").GetInt32();
            return root.GetProperty("entries")[index].GetProperty("transitionType").GetString();
        }
        finally
        {
            await cdp.DetachAsync();
        }
    }

    private string? LastUrl()
    {
        lock (_gate)
        {
            return _actions.LastOrDefault(a => a.Kind == "goto")?.Url;
        }
    }

    private static async Task TryInjectAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(RecorderScript.Source);
        }
        catch (PlaywrightException)
        {
            // Страница ещё грузится — скрипт добавится при загрузке (AddInitScript).
        }
    }

    private void OnRecorded(BindingSource source, string json)
    {
        RecordedAction? action;
        try
        {
            action = JsonSerializer.Deserialize<RecordedAction>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return;
        }

        if (action is null || action.Kind is not ("click" or "fill" or "select" or "check" or "uncheck" or "press"))
        {
            return;
        }

        _lastUserAction = DateTime.UtcNow;
        Add(action);
    }

    private void Add(RecordedAction action)
    {
        lock (_gate)
        {
            // Повторный ввод в то же поле — заменить прежний (пользователь исправлял текст).
            if (action.Kind == "fill" && _actions.Count > 0 && _actions[^1] is { Kind: "fill" } last && last.Selector == action.Selector)
            {
                _actions[^1] = action;
            }
            else
            {
                _actions.Add(action);
            }
        }

        Raise();
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Подписчик (интерфейс) не должен ломать запись.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try
            {
                await sessions.CloseAsync(SessionOwner, SessionName, stopProfile: false, CancellationToken.None);
            }
            catch (Exception ex) when (ex is PlaywrightException or BrowserSessionLostException)
            {
            }
        }

        _lock.Dispose();
    }
}

public sealed class RecorderException(string message) : Exception(message);
