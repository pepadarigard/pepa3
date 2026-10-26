using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using PlaywrightTimeout = System.TimeoutException;

namespace Pepa.Adapters.Browser;

// ───────────── Выбрать в списке ─────────────

public sealed class SelectOptionNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.select";

    private const string ReadSelected =
        "(el, by) => { const o = el.selectedOptions && el.selectedOptions[0]; return o ? (by === 'value' ? o.value : (o.label || o.textContent || '').trim()) : null; }";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Выбрать в списке",
        Description = "Выбирает вариант в выпадающем списке (<select>). Повтор безопасен: выбор просто ставится заново.",
        Icon = "list-checks",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "option",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            BrowserNodes.SelectorField("Список") with { Placeholder = "#country или select[name=city]" },
            new FieldDescriptor { Name = "option", Label = "Вариант", Kind = FieldKind.Template, Required = true, Placeholder = "Россия" },
            new FieldDescriptor
            {
                Name = "by", Label = "Искать вариант", Kind = FieldKind.Select, Default = "label",
                Options = [new("label", "по видимому тексту"), new("value", "по значению (атрибут value)")],
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
            var option = context.RequireString("option");
            var byValue = context.GetString("by") == "value";
            await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector);
            if (!await IsSelectAsync(locator))
            {
                return NodeResult.Fail(
                    $"«{selector}» — не выпадающий список <select>. Если список нарисован сайтом, нажмите на него блоком «Нажать», " +
                    "а затем — на нужный вариант.", retryable: false);
            }

            try
            {
                await locator.SelectOptionAsync(byValue ? new SelectOptionValue { Value = option } : new SelectOptionValue { Label = option },
                    new LocatorSelectOptionOptions { Timeout = BrowserNodes.WaitMs(context) });
            }
            catch (PlaywrightTimeout)
            {
                return NodeResult.Fail($"В списке «{selector}» нет варианта «{option}» (или список неактивен) — ничего не выбрано.");
            }

            return NodeResult.Ok(Ports.Out, $"В «{selector}» выбрано: {option}");
        }, "выбрать в списке");

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var page = await BrowserNodes.PageForVerifyAsync(sessions, context, cancellationToken);
        if (page is null)
        {
            return VerificationResult.Inconclusive("браузер закрыт — проверить нечем");
        }

        try
        {
            var by = context.GetString("by") == "value" ? "value" : "label";
            var selected = await BrowserNodes.Locate(page, context).EvaluateAsync<string?>(ReadSelected, by);
            return selected == context.GetString("option")?.Trim()
                ? VerificationResult.Done("в списке уже выбран нужный вариант")
                : VerificationResult.NotDone($"в списке выбрано другое ({selected ?? "ничего"}) — можно выбрать заново");
        }
        catch (PlaywrightException ex)
        {
            return VerificationResult.Inconclusive($"не удалось прочитать список: {BrowserSessions.FirstLine(ex.Message)}");
        }
    }

    private static Task<bool> IsSelectAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("el => el instanceof HTMLSelectElement");
}

// ───────────── Отметить флажок ─────────────

public sealed class CheckNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.check";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Отметить флажок",
        Description = "Ставит или снимает отметку (флажок, переключатель). Если отметка уже как нужно — ничего не нажимает.",
        Icon = "square-check",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "selector",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            BrowserNodes.SelectorField("Флажок") with { Placeholder = "#agree или text=Согласен" },
            new FieldDescriptor
            {
                Name = "state", Label = "Что сделать", Kind = FieldKind.Select, Default = "check",
                Options = [new("check", "Отметить"), new("uncheck", "Снять отметку")],
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
            var check = context.GetString("state") != "uncheck";
            await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector);
            try
            {
                await locator.SetCheckedAsync(check, new LocatorSetCheckedOptions { Timeout = BrowserNodes.WaitMs(context) });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Not a checkbox", StringComparison.OrdinalIgnoreCase))
            {
                return NodeResult.Fail($"«{selector}» — не флажок и не переключатель. Используйте «Нажать».", retryable: false);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("did not change its state", StringComparison.OrdinalIgnoreCase))
            {
                // Нажатие было. Сайт может менять отметку не сразу (после ответа сервера) — подождать, а не нажимать снова.
                if (await SettledAsync(locator, check))
                {
                    return NodeResult.Ok(Ports.Out, check ? $"Отмечено: «{selector}» (сайт поставил отметку не сразу)." : $"Отметка снята: «{selector}» (не сразу).");
                }

                return new NodeResult.Unknown($"Pepa нажала на «{selector}», но отметка не изменилась — возможно, сайт обрабатывает нажатие иначе. " +
                                              "Ещё одно нажатие могло бы вернуть всё назад, поэтому Pepa не повторяет его сама — посмотрите на страницу.")
                {
                    AlreadyChecked = true,
                };
            }

            return NodeResult.Ok(Ports.Out, check ? $"Отмечено: «{selector}»." : $"Отметка снята: «{selector}».");
        }, "отметить флажок");

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var page = await BrowserNodes.PageForVerifyAsync(sessions, context, cancellationToken);
        if (page is null)
        {
            return VerificationResult.Inconclusive("браузер закрыт — проверить нечем");
        }

        try
        {
            var check = context.GetString("state") != "uncheck";
            return await SettledAsync(BrowserNodes.Locate(page, context), check)
                ? VerificationResult.Done("отметка уже как нужно")
                : VerificationResult.NotDone("отметка не та — можно поставить заново");
        }
        catch (PlaywrightException ex)
        {
            return VerificationResult.Inconclusive($"не удалось проверить отметку: {BrowserSessions.FirstLine(ex.Message)}");
        }
    }

    /// <summary>Отметка стала нужной в течение пары секунд (сайт может ставить её после ответа сервера).</summary>
    private static async Task<bool> SettledAsync(ILocator locator, bool check)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (true)
        {
            if (await locator.IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = 5_000 }) == check)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(200);
        }
    }
}

// ───────────── Загрузить файл ─────────────

public sealed class UploadFileNode(BrowserSessions sessions, IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "browser.upload";

    private const string FileNames = "el => Array.from(el.files || []).map(f => f.name)";

    private const long MaxPayloadBytes = 50L * 1024 * 1024;

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Прикрепить файл",
        Description = "Выбирает файл из рабочей папки в поле загрузки на сайте (как «Обзор…»). Сама отправка формы — отдельным нажатием.",
        Icon = "paperclip",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            BrowserNodes.SelectorField("Поле загрузки") with
            {
                Placeholder = "input[type=file]",
                Help = "Обычно input[type=file]. Поле может быть скрытым — это нормально, Pepa найдёт его в разметке.",
            },
            new FieldDescriptor { Name = "path", Label = "Файл из рабочей папки", Kind = FieldKind.Path, Required = true, Placeholder = "документы/скан.pdf" },
            BrowserNodes.WaitField(),
            BrowserNodes.MatchField,
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input) * 2;

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var full = workspace.Resolve(context.RequireString("path"));
        if (!File.Exists(full))
        {
            throw new NodeInputException($"Файла «{workspace.ToRelative(full)}» нет в рабочей папке.");
        }

        return BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var locator = BrowserNodes.Locate(page, context);
            var selector = context.RequireString("selector");
            await BrowserNodes.WaitReadyAsync(locator, BrowserNodes.WaitMs(context), selector, WaitForSelectorState.Attached);
            var options = new LocatorSetInputFilesOptions { Timeout = BrowserNodes.WaitMs(context) };
            try
            {
                // Содержимым, а не путём: по пути с русскими буквами Chromium молча оставляет поле пустым.
                if (new FileInfo(full).Length <= MaxPayloadBytes)
                {
                    await locator.SetInputFilesAsync(new FilePayload
                    {
                        Name = Path.GetFileName(full),
                        MimeType = MimeType(full),
                        Buffer = await File.ReadAllBytesAsync(full),
                    }, options);
                }
                else if (full.All(char.IsAscii))
                {
                    await locator.SetInputFilesAsync(full, options);
                }
                else
                {
                    return NodeResult.Fail($"Файл больше {MaxPayloadBytes / 1024 / 1024} МБ и в пути есть не латинские буквы — браузер его не примет. " +
                                           "Переложите файл в папку с латинским названием.", retryable: false);
                }
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Node is not an HTMLInputElement", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("Not an input element", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("type=\"file\"", StringComparison.OrdinalIgnoreCase))
            {
                return NodeResult.Fail($"«{selector}» — не поле загрузки файла (нужен input[type=file]).", retryable: false);
            }

            var relative = workspace.ToRelative(full);
            var names = await locator.EvaluateAsync<string[]>(FileNames);
            if (!names.Contains(Path.GetFileName(full), StringComparer.Ordinal))
            {
                // Пустое поле после выбора: файл не принят — или сайт сразу забрал его (так делают многие сайты). Различить нельзя,
                // а повтор мог бы загрузить файл дважды.
                return new NodeResult.Unknown($"Файл «{relative}» выбран, но в поле «{selector}» его не видно: возможно, сайт сразу забрал его " +
                                              "(тогда всё в порядке), а возможно, не принял. Посмотрите на страницу и решите, как продолжить.")
                {
                    AlreadyChecked = true,
                };
            }

            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), new JsonObject { ["path"] = relative },
                $"Файл «{relative}» выбран в «{selector}».");
        }, "прикрепить файл");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var page = await BrowserNodes.PageForVerifyAsync(sessions, context, cancellationToken);
        if (page is null)
        {
            return VerificationResult.Inconclusive("браузер закрыт — проверить нечем");
        }

        try
        {
            var name = Path.GetFileName(workspace.Resolve(context.RequireString("path")));
            var names = await BrowserNodes.Locate(page, context).EvaluateAsync<string[]>(FileNames);
            // Пустое поле ничего не доказывает: сайт мог уже забрать файл. «Не выполнено» сказать нельзя.
            return names.Contains(name, StringComparer.Ordinal)
                ? VerificationResult.Done("файл уже выбран в поле")
                : VerificationResult.Inconclusive("в поле файла нет: либо он не выбирался, либо сайт уже забрал его — посмотрите на страницу");
        }
        catch (PlaywrightException ex)
        {
            return VerificationResult.Inconclusive($"не удалось проверить поле: {BrowserSessions.FirstLine(ex.Message)}");
        }
    }
}

// ───────────── Переключить вкладку ─────────────

public sealed class SwitchTabNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.tab";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Переключить вкладку",
        Description = "Следующие блоки будут работать с другой вкладкой: новой (сайт открыл её после нажатия), первой или найденной " +
                      "по адресу. Ничего не меняет на сайте.",
        Icon = "app-window",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "which",
        Outputs = [new PortDescriptor(Ports.Out, "Переключились"), new PortDescriptor(Ports.Timeout, "Нет такой вкладки", PortKind.Optional), BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "which", Label = "Какая вкладка", Kind = FieldKind.Select, Default = "newest",
                Options = [new("newest", "Новая (открытая последней)"), new("first", "Первая"), new("url", "По адресу")],
                Help = "«Новая» — если последняя открытая вкладка и есть текущая, Pepa подождёт, пока откроется другая.",
            },
            new FieldDescriptor
            {
                Name = "urlContains", Label = "Адрес содержит", Kind = FieldKind.Template, Required = true, Placeholder = "checkout",
                VisibleWhen = new FieldCondition("which", ["url"]),
            },
            BrowserNodes.WaitField(10) with { Label = "Ждать вкладку, секунд", Help = "Сколько ждать, пока нужная вкладка появится." },
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input);

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (session, current) =>
        {
            var which = context.GetString("which") ?? "newest";
            var contains = which == "url" ? context.RequireString("urlContains").Trim() : "";
            var deadline = DateTime.UtcNow + BrowserNodes.WaitSpan(context.Input);
            while (true)
            {
                var pages = session.Context.Pages.Where(p => !p.IsClosed).ToList();
                var target = which switch
                {
                    "first" => pages.FirstOrDefault(),
                    "url" => pages.FirstOrDefault(p => p.Url.Contains(contains, StringComparison.OrdinalIgnoreCase)),
                    _ => pages.LastOrDefault() is { } last && last != current ? last : null,
                };
                if (target is not null)
                {
                    session.Page = target;
                    await target.BringToFrontAsync();
                    var title = await target.TitleAsync();
                    return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
                        new JsonObject { ["url"] = target.Url, ["title"] = title, ["tabs"] = pages.Count },
                        $"Вкладка: {(title.Length > 0 ? title : target.Url)}");
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return NodeResult.Ok(Ports.Timeout, which == "url"
                        ? $"Вкладки с адресом, содержащим «{contains}», нет."
                        : "Новая вкладка не открылась.");
                }

                await Task.Delay(200, cancellationToken);
            }
        }, "переключить вкладку");
}

// ───────────── Ждать адрес страницы ─────────────

public sealed class WaitForUrlNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.waitUrl";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Ждать адрес страницы",
        Description = "Ждёт, пока адрес текущей вкладки будет содержать заданный текст (например, после входа сайт переходит в кабинет).",
        Icon = "link",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "contains",
        Outputs = [new PortDescriptor(Ports.Out, "Дождались"), new PortDescriptor(Ports.Timeout, "Не дождались", PortKind.Optional), BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor { Name = "contains", Label = "Адрес содержит", Kind = FieldKind.Template, Required = true, Placeholder = "/dashboard" },
            BrowserNodes.WaitField(60) with { Label = "Ждать, секунд", Help = "Сколько ждать перехода." },
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input);

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var contains = context.RequireString("contains").Trim();
            try
            {
                await page.WaitForURLAsync(url => url.Contains(contains, StringComparison.OrdinalIgnoreCase),
                    new PageWaitForURLOptions { Timeout = BrowserNodes.WaitMs(context), WaitUntil = WaitUntilState.Commit });
                return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), new JsonObject { ["url"] = page.Url }, $"Адрес: {page.Url}");
            }
            catch (PlaywrightTimeout)
            {
                return NodeResult.Ok(Ports.Timeout, $"Адрес так и не стал содержать «{contains}» (сейчас {page.Url}).");
            }
        }, "ждать адрес");
}

// ───────────── Выполнить JavaScript ─────────────

public sealed class EvaluateNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.evaluate";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Выполнить JavaScript",
        Description = "Выполняет код JavaScript на странице и сохраняет результат. Для опытных: код может изменить страницу, " +
                      "поэтому после сбоя он сам не повторяется. Переменные процесса доступны коду как vars.имя.",
        Icon = "code",
        Effect = NodeEffect.External,
        SupportsVerification = false,
        SummaryField = "script",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "script", Label = "Код", Kind = FieldKind.Code, Required = true,
                Placeholder = "document.querySelectorAll('.item').length",
                Help = "Выражение или функция () => { … return значение; }. Переменные процесса — vars.имя (например, vars.code). " +
                       "Подстановки {{ … }} здесь не работают: текст из переменной не должен становиться кодом.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить результат в", Kind = FieldKind.Variable },
            BrowserNodes.SessionField,
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
    {
        if (node.Config.GetString("script") is { } script && script.Contains("{{", StringComparison.Ordinal))
        {
            yield return new NodeConfigIssue("В коде есть {{ … }}: подстановки в код не делаются. Используйте переменные напрямую: vars.имя.", "script");
        }
    }

    /// <summary>
    /// Странице передаются только переменные, которые код упоминает: vars.имя или, у функции со своим параметром,
    /// v.имя для (v) => …. Если переменные нужны целиком (vars без «.имя», параметр-шаблон { … }) — все.
    /// </summary>
    public static JsonObject UsedVariables(string script, JsonObject variables)
    {
        var name = "vars";
        var declarations = 0;
        if (LooksLikeFunction(script))
        {
            switch (FirstParameter(script))
            {
                case "":
                    return variables;
                case { } parameter:
                    name = parameter;
                    declarations = 1;
                    break;
            }
        }

        var escaped = Regex.Escape(name);
        if (Regex.Matches(script, $@"(?<![\p{{L}}\p{{N}}_$.]){escaped}(?![\p{{L}}\p{{N}}_$])(?!\s*\.)").Count > declarations)
        {
            return variables;
        }

        var used = new JsonObject();
        foreach (Match match in Regex.Matches(script, $@"(?<![\p{{L}}\p{{N}}_$.]){escaped}\s*\.\s*([\p{{L}}_$][\p{{L}}\p{{N}}_$]*)"))
        {
            var variable = match.Groups[1].Value;
            if (!used.ContainsKey(variable) && variables.TryGetPropertyValue(variable, out var value))
            {
                used[variable] = value?.DeepClone();
            }
        }

        return used;
    }

    /// <summary>JSON → обычные значения .NET (так их передаёт Playwright).</summary>
    private static object? Plain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(p => p.Key, p => Plain(p.Value)),
        JsonArray a => a.Select(Plain).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var t) => t,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// Код пользователя на странице; переменные передаются аргументом (vars), а не вклеиваются в текст кода. Функцию
    /// (() => …, async () => …, function (…) { … }) вызываем с vars; выражение или несколько инструкций выполняем как написаны
    /// («….length;», «const n = …; n * 2» — как и раньше) с vars в области видимости.
    /// </summary>
    internal static string Wrap(string script) => LooksLikeFunction(script)
        ? $"async (vars) => {{ {Clean} const __pepa = (\n{script}\n); return __clean(await (typeof __pepa === 'function' ? __pepa(vars) : __pepa), 0); }}"
        : $"async (vars) => {{ {Clean} let __started = false; const __mark = () => {{ __started = true; }}; let __pepa; " +
          // Перед кодом — отметка «начали». Если eval упал, не дойдя до неё, код не разобрался и ничего не выполнялось.
          $"try {{ __pepa = eval({JsonSerializer.Serialize("__mark();\n" + script)}); }} " +
          $"catch (e) {{ if (!__started) throw new Error('{NotParsedMarker}' + (e && e.message)); throw e; }} " +
          "return __clean(await (typeof __pepa === 'function' ? __pepa(vars) : __pepa), 0); }";

    /// <summary>Метка ошибки разбора кода (код не выполнялся) в сообщении об ошибке со страницы.</summary>
    private const string NotParsedMarker = "PEPA_NOT_PARSED: ";

    /// <summary>NaN и бесконечность в результате — пустое значение: в JSON их не записать, а код уже выполнен.</summary>
    private const string Clean =
        "const __clean = (x, d) => typeof x === 'number' ? (Number.isFinite(x) ? x : null) " +
        ": x === null || typeof x !== 'object' || d > 30 ? x " +
        ": Array.isArray(x) ? x.map(i => __clean(i, d + 1)) " +
        ": Object.getPrototypeOf(x) === Object.prototype ? Object.fromEntries(Object.entries(x).map(([k, v]) => [k, __clean(v, d + 1)])) : x;";

    /// <summary>
    /// Проверка разбора кода без выполнения: ошибка в записи кода — значит, на странице ничего не выполнялось
    /// (в отличие от ошибки во время выполнения, после которой страница могла уже измениться).
    /// </summary>
    private const string SyntaxCheck =
        "(src) => { try { new Function(src); return null; } catch (e) { return e instanceof SyntaxError ? String(e.message) : null; } }";

    private static string ParsedSource(string script) => LooksLikeFunction(script) ? $"return (\n{script}\n);" : script;

    private static bool LooksLikeFunction(string script) =>
        Regex.IsMatch(script.TrimStart(), @"^(async\b|function\b|\([^()]*\)\s*=>|[\p{L}_$][\p{L}\p{N}_$]*\s*=>)");

    /// <summary>Первый параметр функции: null — параметров нет, "" — не простое имя (шаблон { … } или [ … ]).</summary>
    private static string? FirstParameter(string script)
    {
        var text = script.TrimStart();
        var list = Regex.Match(text, @"^(?:async\s*)?(?:function\b[^(]*)?\(([^)]*)\)\s*(?:=>|\{)") is { Success: true } wrapped
            ? wrapped.Groups[1].Value
            : Regex.Match(text, @"^(?:async\s+)?([\p{L}_$][\p{L}\p{N}_$]*)\s*=>") is { Success: true } single
                ? single.Groups[1].Value
                : null;
        var first = list?.Split(',')[0].Trim();
        return string.IsNullOrEmpty(first) ? null : Regex.IsMatch(first, @"^[\p{L}_$][\p{L}\p{N}_$]*$") ? first : "";
    }

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var script = context.RequireString("script");
            if (await page.EvaluateAsync<string?>(SyntaxCheck, ParsedSource(script)) is { } syntaxError)
            {
                return NodeResult.Fail($"В коде JavaScript ошибка записи — код не выполнялся: {syntaxError}.", retryable: false);
            }

            JsonElement? result;
            try
            {
                result = await page.EvaluateAsync<JsonElement?>(Wrap(script), Plain(UsedVariables(script, context.Variables)));
            }
            catch (PlaywrightException ex) when (ex.Message.Contains(NotParsedMarker, StringComparison.Ordinal))
            {
                var reason = ex.Message[(ex.Message.IndexOf(NotParsedMarker, StringComparison.Ordinal) + NotParsedMarker.Length)..];
                return NodeResult.Fail($"В коде JavaScript ошибка записи — код не выполнялся: {BrowserSessions.FirstLine(reason)}.", retryable: false);
            }
            catch (PlaywrightException ex) when (!page.IsClosed)
            {
                // Код уже начал выполняться: что он успел изменить на странице, неизвестно — повторять сам по себе нельзя.
                return new NodeResult.Unknown(
                    $"Код JavaScript начал выполняться и завершился ошибкой: {BrowserSessions.FirstLine(ex.Message)}. Что он успел сделать " +
                    "на странице, неизвестно — посмотрите и решите.");
            }
            var value = result is { } element ? JsonSerializer.SerializeToNode(element) : null;
            var updates = new Dictionary<string, JsonNode?>();
            if (context.GetString("saveTo") is { Length: > 0 } saveTo)
            {
                updates[saveTo] = value?.DeepClone();
            }

            var shown = value?.ToJsonString() ?? "нет значения";
            return NodeResult.Ok(Ports.Out, updates, new JsonObject { ["result"] = value },
                $"Код выполнен: {(shown.Length > 80 ? shown[..80] + "…" : shown)}");
        }, "выполнить JavaScript");
}
