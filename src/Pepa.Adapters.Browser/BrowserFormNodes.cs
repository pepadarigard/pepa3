using System.Text.Json;
using System.Text.Json.Nodes;
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
                return new NodeResult.Unknown($"После нажатия на «{selector}» отметка не изменилась — возможно, сайт обрабатывает нажатие иначе.");
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
            var isChecked = await BrowserNodes.Locate(page, context).IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = 5_000 });
            return isChecked == (context.GetString("state") != "uncheck")
                ? VerificationResult.Done("отметка уже как нужно")
                : VerificationResult.NotDone("отметка не та — можно поставить заново");
        }
        catch (PlaywrightException ex)
        {
            return VerificationResult.Inconclusive($"не удалось проверить отметку: {BrowserSessions.FirstLine(ex.Message)}");
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
                return new NodeResult.Unknown($"Браузер не показал файл «{relative}» в поле «{selector}» после выбора — проверьте страницу.");
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
            return names.Contains(name, StringComparer.Ordinal)
                ? VerificationResult.Done("файл уже выбран в поле")
                : VerificationResult.NotDone("файл в поле не выбран — можно выбрать заново");
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
                      "поэтому после сбоя он сам не повторяется.",
        Icon = "code",
        Effect = NodeEffect.External,
        SupportsVerification = false,
        SummaryField = "script",
        Outputs = [BrowserNodes.Out, BrowserNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "script", Label = "Код", Kind = FieldKind.MultilineTemplate, Required = true,
                Placeholder = "document.querySelectorAll('.item').length",
                Help = "Выражение или функция () => { … return значение; }. {{ … }} подставляется в код как текст.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить результат в", Kind = FieldKind.Variable },
            BrowserNodes.SessionField,
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (_, page) =>
        {
            var script = context.RequireString("script");
            var result = await page.EvaluateAsync<JsonElement?>(script);
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
