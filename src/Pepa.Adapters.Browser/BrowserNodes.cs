using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using PlaywrightTimeout = System.TimeoutException;

namespace Pepa.Adapters.Browser;

public static class BrowserNodes
{
    public static readonly NodeCategory Category = new("browser", "Браузер", 20);

    public static NodeRegistry RegisterBrowserNodes(this NodeRegistry registry, BrowserSessions sessions, IWorkspace workspace) => registry
        .RegisterCategory(Category)
        .Register(new OpenBrowserNode(sessions))
        .Register(new OpenAdsPowerNode(sessions))
        .Register(new GotoNode(sessions))
        .Register(new ClickNode(sessions))
        .Register(new FillNode(sessions))
        .Register(new PressNode(sessions))
        .Register(new WaitForElementNode(sessions))
        .Register(new ReadPageNode(sessions))
        .Register(new ScreenshotNode(sessions, workspace))
        .Register(new CloseBrowserNode(sessions));

    // ───────────── Общие поля ─────────────

    internal static readonly FieldDescriptor SessionField = new()
    {
        Name = "session", Label = "Окно браузера", Kind = FieldKind.Text, Default = "main", Required = true,
        Help = "Имя открытого браузера, с которым работает блок. Если браузер в процессе один — оставьте «main».",
    };

    internal static FieldDescriptor SelectorField(string label = "Элемент", bool required = true) => new()
    {
        Name = "selector", Label = label, Kind = FieldKind.Template, Required = required, Placeholder = "#login или text=Войти",
        Help = "CSS-селектор (#id, .класс, button[type=submit]) или текст: text=Войти. Также role=button[name=\"Войти\"].",
    };

    internal static readonly FieldDescriptor MatchField = new()
    {
        Name = "match", Label = "Если подходит несколько элементов", Kind = FieldKind.Select, Default = "strict",
        Options = [new("strict", "Остановиться с ошибкой"), new("first", "Взять первый"), new("last", "Взять последний")],
    };

    internal static FieldDescriptor WaitField(int seconds = 30) => new()
    {
        Name = "waitSeconds", Label = "Ждать элемент, секунд", Kind = FieldKind.Seconds, Default = seconds, Min = 1, Max = 600,
        Help = "Сколько ждать, пока элемент появится и станет доступен.",
    };

    internal static readonly PortDescriptor Out = new(Ports.Out, "Далее");
    internal static readonly PortDescriptor Error = new(Ports.Error, "Ошибка", PortKind.Error);

    internal static string SessionName(NodeExecutionContext context) => context.GetString("session") is { Length: > 0 } name ? name.Trim() : "main";

    internal static float WaitMs(NodeExecutionContext context) => (float)(Math.Clamp(context.GetNumber("waitSeconds") ?? 30, 1, 600) * 1000);

    internal static TimeSpan WaitSpan(JsonObject input) => TimeSpan.FromSeconds(Math.Clamp(input.GetNumber("waitSeconds") ?? 30, 1, 600));

    internal static ILocator Locate(IPage page, NodeExecutionContext context)
    {
        var selector = context.RequireString("selector").Trim();
        var locator = page.Locator(selector);
        return context.GetString("match") switch
        {
            "first" => locator.First,
            "last" => locator.Last,
            _ => locator,
        };
    }

    /// <summary>Выполнить действие на странице и перевести ошибки Playwright в честный итог шага.</summary>
    internal static async ValueTask<NodeResult> Run(NodeExecutionContext context, BrowserSessions sessions, CancellationToken cancellationToken,
        Func<BrowserSession, IPage, Task<NodeResult>> action, string what)
    {
        BrowserSession session;
        IPage page;
        try
        {
            session = await sessions.GetAsync(context.ProcessId, SessionName(context), cancellationToken);
            page = session.CurrentPage();
        }
        catch (BrowserSessionLostException lost)
        {
            return NodeResult.Fail(lost.Message, retryable: false);
        }
        catch (AdsPowerException ads)
        {
            return NodeResult.Fail(ads.Message, retryable: true);
        }

        try
        {
            return await action(session, page).WaitAsync(cancellationToken);
        }
        catch (ElementNotReadyException notReady)
        {
            return NodeResult.Fail(notReady.Message);
        }
        catch (PlaywrightException ex) when (IsStrictViolation(ex))
        {
            return NodeResult.Fail(
                $"Под селектор подходит несколько элементов, и Pepa не знает, какой из них нужен. Уточните селектор или выберите «Взять первый». ({BrowserSessions.FirstLine(ex.Message)})",
                retryable: false);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Unexpected token", StringComparison.Ordinal) ||
                                             ex.Message.Contains("is not a valid selector", StringComparison.Ordinal))
        {
            return NodeResult.Fail($"Селектор записан с ошибкой: {BrowserSessions.FirstLine(ex.Message)}", retryable: false);
        }
        catch (PlaywrightException ex) when (session.Closed || page.IsClosed)
        {
            return new NodeResult.Unknown($"Браузер или вкладка закрылись во время действия «{what}»: {BrowserSessions.FirstLine(ex.Message)}");
        }
        catch (PlaywrightException ex)
        {
            return new NodeResult.Unknown($"Ошибка браузера во время действия «{what}»: {BrowserSessions.FirstLine(ex.Message)}");
        }
        catch (PlaywrightTimeout ex)
        {
            return new NodeResult.Unknown($"Браузер не ответил вовремя во время действия «{what}»: {BrowserSessions.FirstLine(ex.Message)}");
        }
    }

    private static bool IsStrictViolation(PlaywrightException ex) =>
        ex.Message.Contains("strict mode violation", StringComparison.OrdinalIgnoreCase);

    /// <summary>Дождаться, пока элемент появится; иначе — действие точно не выполнялось.</summary>
    internal static async Task WaitReadyAsync(ILocator locator, float timeoutMs, string selector, WaitForSelectorState state = WaitForSelectorState.Visible)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = state, Timeout = timeoutMs });
        }
        catch (PlaywrightTimeout)
        {
            throw new ElementNotReadyException($"Элемент «{selector}» не появился за {timeoutMs / 1000:0} с — действие не выполнялось.");
        }
    }

    /// <summary>Сессию для проверки результата: без переподключения (если её нет — проверить нечем).</summary>
    internal static async Task<IPage?> PageForVerifyAsync(BrowserSessions sessions, NodeExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var session = await sessions.GetAsync(context.ProcessId, SessionName(context), cancellationToken);
            return session.CurrentPage();
        }
        catch (Exception ex) when (ex is BrowserSessionLostException or AdsPowerException)
        {
            return null;
        }
    }

    internal static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');
}

/// <summary>Элемент не появился — действие на странице не начиналось.</summary>
internal sealed class ElementNotReadyException(string message) : Exception(message);

// ───────────── Открыть обычный браузер ─────────────

public sealed class OpenBrowserNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.open";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Открыть браузер",
        Description = "Запускает Microsoft Edge или Google Chrome, с которым работают следующие блоки браузера.",
        Icon = "globe",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "url",
        ResourceHint = "Если сохранять вход на сайты, профиль браузера Pepa занимается этим процессом — другие процессы с тем же профилем подождут.",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "browser", Label = "Браузер", Kind = FieldKind.Select, Default = "msedge",
                Options = [new("msedge", "Microsoft Edge"), new("chrome", "Google Chrome")],
            },
            new FieldDescriptor { Name = "url", Label = "Открыть адрес", Kind = FieldKind.Template, Placeholder = "https://example.com" },
            new FieldDescriptor
            {
                Name = "keepLogin", Label = "Сохранять вход на сайты", Kind = FieldKind.Boolean, Default = true,
                Help = "Отдельный профиль браузера Pepa: cookies и вход сохраняются между запусками. Ваш обычный профиль не используется.",
            },
            new FieldDescriptor
            {
                Name = "profile", Label = "Профиль браузера Pepa", Kind = FieldKind.Template, Default = "main",
                VisibleWhen = new FieldCondition("keepLogin", ["true"]),
                Help = "Разные имена — разные наборы cookies. Один профиль одновременно используется одним процессом.",
            },
            new FieldDescriptor { Name = "headless", Label = "Без окна (в фоне)", Kind = FieldKind.Boolean, Default = false },
            BrowserNodes.SessionField,
        ],
    };

    private static bool KeepLogin(JsonObject input) => input.GetBoolean("keepLogin") ?? true;

    private static string ProfileName(JsonObject input) =>
        input.GetString("profile") is { Length: > 0 } name && name.Trim().Length > 0 ? name.Trim() : "main";

    public ResourceRequirement? RequiredResource(JsonObject input) => KeepLogin(input)
        ? new ResourceRequirement($"pepa-browser:{ProfileName(input)}", $"Профиль браузера Pepa «{ProfileName(input)}»")
        : null;

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var name = BrowserNodes.SessionName(context);
        var keep = KeepLogin(context.Input);
        var options = new LocalBrowserOptions
        {
            Channel = context.GetString("browser") == "chrome" ? "chrome" : "msedge",
            Headless = context.GetBoolean("headless"),
            UserDataDir = keep ? Path.Combine(sessions.ProfilesDirectory, SafeName(ProfileName(context.Input))) : null,
        };

        BrowserSession session;
        try
        {
            session = await sessions.OpenLocalAsync(context.ProcessId, name, options, RequiredResource(context.Input)?.Key,
                closeOnFinish: true, cancellationToken);
        }
        catch (BrowserStartException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        return await NavigateAfterOpenAsync(session, context.GetString("url"), "Браузер открыт", cancellationToken);
    }

    internal static async Task<NodeResult> NavigateAfterOpenAsync(BrowserSession session, string? url, string opened,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), new JsonObject { ["session"] = session.Name }, $"{opened}.");
        }

        try
        {
            await session.CurrentPage().GotoAsync(url.Trim(), new PageGotoOptions { Timeout = 60_000 }).WaitAsync(cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            return NodeResult.Fail($"{opened}, но страница {url} не открылась: {BrowserSessions.FirstLine(ex.Message)}");
        }

        return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            new JsonObject { ["session"] = session.Name, ["url"] = session.CurrentPage().Url }, $"{opened}: {url}");
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(sessions.IsOpen(context.ProcessId, BrowserNodes.SessionName(context))
            ? VerificationResult.Done("браузер открыт")
            : VerificationResult.NotDone("браузер не открыт"));

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*', '/', '\\']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return safe.Length == 0 ? "main" : safe;
    }
}

// ───────────── Профиль AdsPower ─────────────

public sealed class OpenAdsPowerNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "adspower.open";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Открыть профиль AdsPower",
        Description = "Открывает существующий профиль AdsPower через Local API и подключается к его браузеру. " +
                      "Нужно запущенное приложение AdsPower с включённым Local API.",
        Icon = "fingerprint",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "profileId",
        ResourceHint = "Профиль AdsPower занимается этим процессом: другой процесс с тем же профилем подождёт, пока этот его закроет или завершится.",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "profileId", Label = "ID профиля", Kind = FieldKind.Template, Required = true, Placeholder = "k1bitq6l",
                Lookup = "adspower-profiles",
                Help = "ID профиля из списка профилей AdsPower (колонка «ID»). Можно подставить из переменной: {{ vars.profile }}.",
            },
            new FieldDescriptor { Name = "url", Label = "Открыть адрес", Kind = FieldKind.Template, Placeholder = "https://example.com" },
            new FieldDescriptor { Name = "newTab", Label = "Открыть в новой вкладке", Kind = FieldKind.Boolean, Default = false },
            new FieldDescriptor
            {
                Name = "closeOnFinish", Label = "Закрыть профиль, когда процесс завершится", Kind = FieldKind.Boolean, Default = true,
            },
            BrowserNodes.SessionField,
        ],
    };

    private static string ProfileId(JsonObject input) => input.GetString("profileId")?.Trim() ?? "";

    public ResourceRequirement? RequiredResource(JsonObject input) => ProfileId(input) is { Length: > 0 } id
        ? new ResourceRequirement(BrowserSessions.AdsPowerResourceKey(id), $"Профиль AdsPower {id}")
        : null;

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var profileId = ProfileId(context.Input);
        if (profileId.Length == 0)
        {
            throw new NodeInputException("Не указан ID профиля AdsPower.");
        }

        BrowserSession session;
        try
        {
            session = await sessions.OpenAdsPowerAsync(context.ProcessId, BrowserNodes.SessionName(context), profileId,
                context.GetBoolean("newTab"), context.GetBoolean("closeOnFinish", true), cancellationToken);
        }
        catch (AdsPowerException ex)
        {
            return NodeResult.Fail(ex.Message);
        }
        catch (BrowserStartException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        return await OpenBrowserNode.NavigateAfterOpenAsync(session, context.GetString("url"), $"Профиль AdsPower {profileId} открыт",
            cancellationToken);
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var state = await sessions.AdsPower.GetStateAsync(ProfileId(context.Input), cancellationToken);
            return state.Active
                ? VerificationResult.Done("профиль открыт в AdsPower")
                : VerificationResult.NotDone("профиль в AdsPower не открыт");
        }
        catch (AdsPowerException ex)
        {
            return VerificationResult.Inconclusive($"Не удалось спросить AdsPower: {ex.Message}");
        }
    }
}

// ───────────── Перейти по адресу ─────────────

public sealed class GotoNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.goto";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Перейти по адресу",
        Description = "Открывает страницу в текущей вкладке и ждёт её загрузки.",
        Icon = "link",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "url",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor { Name = "url", Label = "Адрес", Kind = FieldKind.Template, Required = true, Placeholder = "https://example.com/login" },
            new FieldDescriptor
            {
                Name = "waitUntil", Label = "Считать загруженной, когда", Kind = FieldKind.Select, Default = "load",
                Options = [new("load", "страница загрузилась"), new("domcontentloaded", "готова разметка (быстрее)"), new("networkidle", "сеть затихла (медленнее)")],
            },
            new FieldDescriptor { Name = "waitSeconds", Label = "Ждать загрузку, секунд", Kind = FieldKind.Seconds, Default = 60, Min = 1, Max = 600 },
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input);

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var url = context.RequireString("url").Trim();
            var waitUntil = context.GetString("waitUntil") switch
            {
                "domcontentloaded" => WaitUntilState.DOMContentLoaded,
                "networkidle" => WaitUntilState.NetworkIdle,
                _ => WaitUntilState.Load,
            };
            try
            {
                var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = waitUntil, Timeout = BrowserNodes.WaitMs(context) });
                var status = response?.Status;
                return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
                    new JsonObject { ["url"] = page.Url, ["status"] = status },
                    status is >= 400 ? $"Открыта страница {page.Url}, но сайт ответил кодом {status}." : $"Открыта страница {page.Url}.");
            }
            catch (PlaywrightTimeout)
            {
                return new NodeResult.Unknown($"Страница {url} не загрузилась за отведённое время.");
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR_", StringComparison.Ordinal))
            {
                return NodeResult.Fail($"Страница {url} не открылась: {BrowserSessions.FirstLine(ex.Message)}");
            }
        }, "перейти по адресу");

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var page = await BrowserNodes.PageForVerifyAsync(sessions, context, cancellationToken);
        if (page is null)
        {
            return VerificationResult.Inconclusive("браузер закрыт — проверить нечем");
        }

        var target = BrowserNodes.NormalizeUrl(context.RequireString("url"));
        var current = BrowserNodes.NormalizeUrl(page.Url);
        return current.StartsWith(target, StringComparison.OrdinalIgnoreCase)
            ? VerificationResult.Done($"открыта страница {page.Url}")
            : VerificationResult.NotDone($"открыта другая страница: {page.Url}");
    }
}

// ───────────── Нажать ─────────────

public sealed class ClickNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.click";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Нажать на элемент",
        Description = "Ждёт, пока элемент станет доступен, и нажимает на него.",
        Icon = "mouse-pointer-click",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "selector",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            BrowserNodes.SelectorField(),
            new FieldDescriptor
            {
                Name = "clicks", Label = "Как нажать", Kind = FieldKind.Select, Default = "single",
                Options = [new("single", "Один раз"), new("double", "Двойной щелчок"), new("right", "Правой кнопкой")],
            },
            new FieldDescriptor
            {
                Name = "expect", Label = "После нажатия должен появиться", Kind = FieldKind.Template, Placeholder = "text=Готово",
                Help = "Необязательно. Элемент, по которому видно, что нажатие сработало. Pepa дождётся его, а после сбоя — проверит по нему, не нажимая повторно.",
            },
            BrowserNodes.WaitField(),
            BrowserNodes.MatchField,
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input) * 2;

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var locator = BrowserNodes.Locate(page, context);
            var selector = context.RequireString("selector");
            await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector);
            try
            {
                switch (context.GetString("clicks"))
                {
                    case "double":
                        await locator.DblClickAsync(new LocatorDblClickOptions { Timeout = BrowserNodes.WaitMs(context) });
                        break;
                    case "right":
                        await locator.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right, Timeout = BrowserNodes.WaitMs(context) });
                        break;
                    default:
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = BrowserNodes.WaitMs(context) });
                        break;
                }
            }
            catch (PlaywrightTimeout)
            {
                // Playwright не нажимает, пока элемент не готов: по тайм-ауту нажатия не было.
                return NodeResult.Fail($"Элемент «{selector}» так и не стал доступен для нажатия (закрыт другим элементом или неактивен).");
            }

            if (context.GetString("expect") is { Length: > 0 } expect)
            {
                try
                {
                    await page.Locator(expect).First.WaitForAsync(new LocatorWaitForOptions { Timeout = BrowserNodes.WaitMs(context) });
                }
                catch (PlaywrightTimeout)
                {
                    return new NodeResult.Unknown($"Нажатие на «{selector}» выполнено, но «{expect}» не появился — неизвестно, сработало ли нажатие.");
                }
            }

            return NodeResult.Ok(Ports.Out, $"Нажато: {selector}.");
        }, "нажать");

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.GetString("expect") is not { Length: > 0 } expect)
        {
            return VerificationResult.NotSupported();
        }

        var page = await BrowserNodes.PageForVerifyAsync(sessions, context, cancellationToken);
        if (page is null)
        {
            return VerificationResult.Inconclusive("браузер закрыт — проверить нечем");
        }

        return await page.Locator(expect).CountAsync() > 0
            ? VerificationResult.Done($"на странице есть «{expect}»")
            : VerificationResult.Inconclusive($"«{expect}» на странице нет — нажатие могло не сработать или страница изменилась");
    }
}

// ───────────── Ввести текст ─────────────

public sealed class FillNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.fill";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Ввести текст",
        Description = "Очищает поле ввода и вводит текст. Повтор безопасен: поле каждый раз заполняется заново.",
        Icon = "text-cursor-input",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "selector",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            BrowserNodes.SelectorField("Поле ввода"),
            new FieldDescriptor { Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate, Placeholder = "{{ vars.login }}" },
            new FieldDescriptor
            {
                Name = "mode", Label = "Как вводить", Kind = FieldKind.Select, Default = "fill",
                Options = [new("fill", "Сразу целиком"), new("type", "По символам, как человек")],
                Help = "«По символам» медленнее, но нужен сайтам, которые следят за нажатиями клавиш.",
            },
            BrowserNodes.WaitField(),
            BrowserNodes.MatchField,
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) =>
        BrowserNodes.WaitSpan(input) + TimeSpan.FromMilliseconds(120 * (input.GetString("text")?.Length ?? 0));

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var locator = BrowserNodes.Locate(page, context);
            var selector = context.RequireString("selector");
            var text = context.GetString("text") ?? "";
            await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector);
            try
            {
                if (context.GetString("mode") == "type")
                {
                    await locator.FillAsync("", new LocatorFillOptions { Timeout = BrowserNodes.WaitMs(context) });
                    await locator.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = 60 });
                }
                else
                {
                    await locator.FillAsync(text, new LocatorFillOptions { Timeout = BrowserNodes.WaitMs(context) });
                }
            }
            catch (PlaywrightTimeout)
            {
                return NodeResult.Fail($"В поле «{selector}» нельзя ввести текст (оно неактивно или это не поле ввода).");
            }

            return NodeResult.Ok(Ports.Out, $"Введено в «{selector}»: {Preview(text)}");
        }, "ввести текст");

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var page = await BrowserNodes.PageForVerifyAsync(sessions, context, cancellationToken);
        if (page is null)
        {
            return VerificationResult.Inconclusive("браузер закрыт — проверить нечем");
        }

        try
        {
            var value = await BrowserNodes.Locate(page, context).InputValueAsync(new LocatorInputValueOptions { Timeout = 5_000 });
            return value == (context.GetString("text") ?? "")
                ? VerificationResult.Done("в поле уже нужный текст")
                : VerificationResult.NotDone("в поле другой текст — можно ввести заново");
        }
        catch (PlaywrightException ex)
        {
            return VerificationResult.Inconclusive($"не удалось прочитать поле: {BrowserSessions.FirstLine(ex.Message)}");
        }
    }

    private static string Preview(string text) => text.Length > 40 ? text[..40] + "…" : text;
}

// ───────────── Нажать клавишу ─────────────

public sealed class PressNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.press";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Нажать клавишу",
        Description = "Нажимает клавишу или сочетание клавиш на странице (например, Enter).",
        Icon = "keyboard",
        Effect = NodeEffect.External,
        SupportsVerification = false,
        SummaryField = "key",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "key", Label = "Клавиша", Kind = FieldKind.Text, Required = true, Default = "Enter",
                Help = "Enter, Tab, Escape, ArrowDown, Control+A и т.п.",
            },
            BrowserNodes.SelectorField("В элементе", required: false),
            BrowserNodes.WaitField(),
            BrowserNodes.SessionField,
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var key = context.RequireString("key").Trim();
            if (context.GetString("selector") is { Length: > 0 } selector)
            {
                var locator = page.Locator(selector.Trim());
                await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector);
                await locator.PressAsync(key, new LocatorPressOptions { Timeout = BrowserNodes.WaitMs(context) });
            }
            else
            {
                await page.Keyboard.PressAsync(key);
            }

            return NodeResult.Ok(Ports.Out, $"Нажата клавиша {key}.");
        }, "нажать клавишу");
}

// ───────────── Ждать элемент ─────────────

public sealed class WaitForElementNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.waitFor";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Ждать элемент",
        Description = "Ждёт, пока элемент появится (или исчезнет) на странице. Ничего не меняет.",
        Icon = "scan-search",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "selector",
        Outputs = [new PortDescriptor(Ports.Out, "Дождались"), new PortDescriptor(Ports.Timeout, "Не дождались", PortKind.Optional), BrowserNodes.Error],
        Fields =
        [
            BrowserNodes.SelectorField(),
            new FieldDescriptor
            {
                Name = "state", Label = "Ждать, пока элемент", Kind = FieldKind.Select, Default = "visible",
                Options = [new("visible", "появится"), new("hidden", "исчезнет"), new("attached", "появится в разметке (даже скрытый)")],
            },
            BrowserNodes.WaitField(60),
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input);

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var selector = context.RequireString("selector");
            var state = context.GetString("state") switch
            {
                "hidden" => WaitForSelectorState.Hidden,
                "attached" => WaitForSelectorState.Attached,
                _ => WaitForSelectorState.Visible,
            };
            try
            {
                await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions { State = state, Timeout = BrowserNodes.WaitMs(context) });
                return NodeResult.Ok(Ports.Out, state == WaitForSelectorState.Hidden ? $"«{selector}» исчез." : $"«{selector}» появился.");
            }
            catch (PlaywrightTimeout)
            {
                return NodeResult.Ok(Ports.Timeout, $"Не дождались «{selector}» за {BrowserNodes.WaitMs(context) / 1000:0} с.");
            }
        }, "ждать элемент");
}

// ───────────── Прочитать со страницы ─────────────

public sealed class ReadPageNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.read";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Прочитать со страницы",
        Description = "Сохраняет в переменную текст, значение, атрибут элемента, число элементов, адрес или заголовок страницы.",
        Icon = "file-search",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "selector",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "what", Label = "Что прочитать", Kind = FieldKind.Select, Default = "text",
                Options =
                [
                    new("text", "Текст элемента"), new("texts", "Тексты всех подходящих элементов (список)"),
                    new("value", "Значение поля ввода"), new("attribute", "Атрибут элемента"),
                    new("count", "Сколько элементов"), new("exists", "Есть ли элемент (да/нет)"),
                    new("url", "Адрес страницы"), new("title", "Заголовок страницы"),
                ],
            },
            BrowserNodes.SelectorField() with { Required = false, VisibleWhen = new FieldCondition("what", ["text", "texts", "value", "attribute", "count", "exists"]) },
            new FieldDescriptor
            {
                Name = "attribute", Label = "Атрибут", Kind = FieldKind.Text, Placeholder = "href", Required = true,
                VisibleWhen = new FieldCondition("what", ["attribute"]),
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить в", Kind = FieldKind.Variable, Required = true },
            BrowserNodes.WaitField(10) with { VisibleWhen = new FieldCondition("what", ["text", "value", "attribute"]) },
            BrowserNodes.SessionField,
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
    {
        var what = node.Config.GetString("what") ?? "text";
        if (what is "text" or "texts" or "value" or "attribute" or "count" or "exists" && string.IsNullOrWhiteSpace(node.Config.GetString("selector")))
        {
            yield return new NodeConfigIssue("укажите элемент, который нужно прочитать.", "selector");
        }
    }

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var saveTo = context.RequireString("saveTo");
            var what = context.GetString("what") ?? "text";
            JsonNode? value;
            switch (what)
            {
                case "url":
                    value = page.Url;
                    break;
                case "title":
                    value = await page.TitleAsync();
                    break;
                case "count":
                    value = await page.Locator(context.RequireString("selector")).CountAsync();
                    break;
                case "exists":
                    value = await page.Locator(context.RequireString("selector")).CountAsync() > 0;
                    break;
                case "texts":
                    value = new JsonArray((await page.Locator(context.RequireString("selector")).AllInnerTextsAsync())
                        .Select(t => (JsonNode?)JsonValue.Create(t.Trim())).ToArray());
                    break;
                default:
                {
                    var selector = context.RequireString("selector");
                    var locator = BrowserNodes.Locate(page, context with { Input = WithStrictFirst(context.Input) });
                    await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector, WaitForSelectorState.Attached);
                    value = what switch
                    {
                        "value" => await locator.InputValueAsync(),
                        "attribute" => await locator.GetAttributeAsync(context.RequireString("attribute").Trim()),
                        _ => (await locator.InnerTextAsync()).Trim(),
                    };
                    break;
                }
            }

            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?> { [saveTo] = value }, null,
                $"Прочитано в «{saveTo}»: {Preview(value)}");
        }, "прочитать со страницы");

    /// <summary>Чтение не меняет страницу: если подходит несколько элементов, берём первый.</summary>
    private static JsonObject WithStrictFirst(JsonObject input)
    {
        var copy = (JsonObject)input.DeepClone();
        copy["match"] = "first";
        return copy;
    }

    private static string Preview(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "пусто";
        return text.Length > 80 ? text[..80] + "…" : text;
    }
}

// ───────────── Снимок экрана ─────────────

public sealed class ScreenshotNode(BrowserSessions sessions, IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "browser.screenshot";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Снимок страницы",
        Description = "Сохраняет снимок страницы (или одного элемента) в файл PNG в рабочей папке.",
        Icon = "camera",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "path", Label = "Файл", Kind = FieldKind.Path, Required = true, Default = "снимки/{{ process.id }}.png",
            },
            new FieldDescriptor { Name = "fullPage", Label = "Вся страница целиком", Kind = FieldKind.Boolean, Default = false },
            BrowserNodes.SelectorField("Только элемент", required: false),
            BrowserNodes.SessionField,
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var full = workspace.Resolve(context.RequireString("path"));
        if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            full += ".png";
        }

        return BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (context.GetString("selector") is { Length: > 0 } selector)
            {
                var locator = page.Locator(selector).First;
                await BrowserNodes.WaitReadyAsync(locator, 10_000, selector);
                await locator.ScreenshotAsync(new LocatorScreenshotOptions { Path = full });
            }
            else
            {
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = full, FullPage = context.GetBoolean("fullPage") });
            }

            var relative = workspace.ToRelative(full);
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), new JsonObject { ["path"] = relative }, $"Снимок сохранён: {relative}");
        }, "снимок страницы");
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var full = workspace.Resolve(context.RequireString("path"));
        if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            full += ".png";
        }

        return ValueTask.FromResult(File.Exists(full) && new FileInfo(full).Length > 0
            ? VerificationResult.Done("файл снимка есть")
            : VerificationResult.NotDone("файла снимка нет"));
    }
}

// ───────────── Закрыть браузер ─────────────

public sealed class CloseBrowserNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.close";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Закрыть браузер",
        Description = "Закрывает браузер (или профиль AdsPower) и освобождает его для других процессов.",
        Icon = "circle-x",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "session",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "stopProfile", Label = "Закрыть профиль в AdsPower", Kind = FieldKind.Boolean, Default = true,
                Help = "Только для профилей AdsPower. Если выключить — Pepa отключится от браузера, а профиль останется открытым.",
            },
            BrowserNodes.SessionField,
        ],
    };

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var name = BrowserNodes.SessionName(context);
        BrowserSessionInfo? info;
        try
        {
            info = await sessions.CloseAsync(context.ProcessId, name, context.GetBoolean("stopProfile", true), cancellationToken);
        }
        catch (AdsPowerException ex)
        {
            return NodeResult.Fail($"Pepa отключилась от браузера, но закрыть профиль в AdsPower не удалось: {ex.Message}");
        }

        if (info is null)
        {
            return new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), null,
                $"Браузер «{name}» уже был закрыт.", MessageLevel.Warning);
        }

        return new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), null,
            info.Kind == BrowserKind.AdsPower ? $"Профиль AdsPower {info.ProfileId} закрыт." : $"Браузер «{name}» закрыт.")
        {
            ReleaseResources = info.ResourceKey is { } key ? [key] : null,
        };
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(sessions.IsOpen(context.ProcessId, BrowserNodes.SessionName(context))
            ? VerificationResult.NotDone("браузер ещё открыт")
            : VerificationResult.Done("браузер закрыт"));
}
