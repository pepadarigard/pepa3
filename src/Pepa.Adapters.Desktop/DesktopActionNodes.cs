using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Desktop;

// ───────────── Запустить программу ─────────────

public sealed class StartProgramNode(IDesktop desktop) : INodeExecutor
{
    public const string TypeName = "desktop.start";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Запустить программу",
        Description = "Запускает программу и при необходимости ждёт её окно. Если указано окно, после сбоя Pepa проверит, " +
                      "открыто ли оно, а не запустит программу второй раз.",
        Icon = "rocket",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [new PortDescriptor(Ports.Out, "Запущена"), new PortDescriptor(Ports.Timeout, "Окно не появилось", PortKind.Optional), DesktopNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "path", Label = "Программа", Kind = FieldKind.Template, Required = true,
                Placeholder = @"C:\Program Files\Telegram Desktop\Telegram.exe",
                Help = "Полный путь к .exe или имя программы из Windows (notepad, calc).",
            },
            new FieldDescriptor { Name = "arguments", Label = "Параметры запуска", Kind = FieldKind.Template },
            DesktopNodes.WindowField with { Label = "Дождаться окна (часть заголовка)", Help = "Необязательно. Пусто — не ждать окна." },
            DesktopNodes.WaitField(60) with { Label = "Ждать окно, секунд" },
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => DesktopNodes.WaitSpan(input);

    /// <summary>До запуска: какие окна с этим заголовком уже открыты — чтобы ждать и проверять именно новое окно.</summary>
    public async ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.GetString("window") is not { Length: > 0 } title || desktop.UnavailableReason is not null)
        {
            return null;
        }

        try
        {
            var windows = await desktop.FindWindowsAsync(new WindowQuery(title.Trim(), null), cancellationToken);
            return new JsonObject { ["before"] = new JsonArray(windows.Select(w => (JsonNode)w.Info.Handle).ToArray()) };
        }
        catch (DesktopException)
        {
            // Список окон не получен — новое окно от старого не отличить; блок ждёт любое подходящее.
            return null;
        }
    }

    private static HashSet<long>? WindowsBefore(NodeExecutionContext context) =>
        context.Checkpoint?["before"] is JsonArray before ? before.Select(n => n!.GetValue<long>()).ToHashSet() : null;

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is { } reason)
        {
            return NodeResult.Fail(reason, retryable: false);
        }

        var path = context.RequireString("path").Trim().Trim('"');
        int processId;
        try
        {
            processId = await desktop.StartProgramAsync(path, context.GetString("arguments"), cancellationToken);
        }
        catch (DesktopException ex)
        {
            // Программа не запустилась (нет файла, нет доступа) — Windows сообщает об этом сразу.
            return NodeResult.Fail($"Не удалось запустить «{path}»: {ex.Message}", retryable: false);
        }

        var output = new JsonObject { ["processId"] = processId };
        if (context.GetString("window") is not { Length: > 0 } title)
        {
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"Запущена программа «{path}».");
        }

        var before = WindowsBefore(context);
        var query = new WindowQuery(title.Trim(), null);
        DesktopWindow? window;
        try
        {
            window = await DesktopNodes.WaitWindowAsync(desktop, query, DesktopNodes.WaitSpan(context.Input), cancellationToken,
                before is null ? null : w => !before.Contains(w.Info.Handle));
        }
        catch (DesktopAmbiguousException)
        {
            // Новых окон с таким заголовком несколько — программа запущена, а окно точно есть.
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"Запущена «{path}»; окон «{title}» несколько.");
        }
        catch (DesktopException ex)
        {
            // Программа запущена; не получилось только проверить окна.
            return NodeResult.Ok(Ports.Timeout, new Dictionary<string, JsonNode?>(), output,
                $"Программа «{path}» запущена, но проверить её окно не удалось: {ex.Message}");
        }

        if (window is null)
        {
            if (before is { Count: > 0 })
            {
                // Программа, видимо, уже была запущена: второе окно она не открыла, а показала прежнее.
                return new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), output,
                    $"Программа «{path}» запущена, но нового окна «{title}» нет — открыто прежнее (программа, видимо, уже работала).",
                    MessageLevel.Warning);
            }

            return NodeResult.Ok(Ports.Timeout, new Dictionary<string, JsonNode?>(), output, $"Программа «{path}» запущена, но окно «{title}» не появилось.");
        }

        output["window"] = window.Info.Title;
        return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"Запущена «{path}», окно «{window.Info.Title}».");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.GetString("window") is not { Length: > 0 } title || desktop.UnavailableReason is not null)
        {
            return VerificationResult.NotSupported();
        }

        try
        {
            var windows = await desktop.FindWindowsAsync(new WindowQuery(title.Trim(), null), cancellationToken);
            var before = WindowsBefore(context);
            var fresh = windows.Where(w => before is null || !before.Contains(w.Info.Handle)).ToList();
            if (fresh.Count > 0)
            {
                return VerificationResult.Done($"окно «{fresh[0].Info.Title}» открыто");
            }

            return windows.Count > 0
                ? VerificationResult.Inconclusive($"окно «{windows[0].Info.Title}» было открыто и до запуска — запустилась ли программа ещё раз, не видно; решите сами")
                : VerificationResult.Inconclusive($"окна «{title}» нет — но программа могла запуститься без окна; решите сами");
        }
        catch (DesktopException ex)
        {
            return VerificationResult.Inconclusive($"не удалось проверить окна: {ex.Message}");
        }
    }
}

// ───────────── Найти окно ─────────────

public sealed class FindWindowNode(IDesktop desktop) : INodeExecutor
{
    public const string TypeName = "desktop.window";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Найти окно",
        Description = "Ждёт окно программы и (по желанию) выводит его на передний план. Ничего в окне не нажимает.",
        Icon = "app-window",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "window",
        ResourceHint = "Если включено «Вывести на передний план», блок занимает мышь и клавиатуру (ресурс «desktop:input»).",
        Outputs = [new PortDescriptor(Ports.Out, "Найдено"), new PortDescriptor(Ports.Timeout, "Не найдено", PortKind.Optional), DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField,
            DesktopNodes.ProcessField,
            new FieldDescriptor { Name = "activate", Label = "Вывести на передний план", Kind = FieldKind.Boolean, Default = true },
            DesktopNodes.WaitField(),
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить сведения об окне в", Kind = FieldKind.Variable },
        ],
    };

    public ResourceRequirement? RequiredResource(JsonObject input) =>
        input.GetBoolean("activate") ?? true ? DesktopNodes.InputResource : null;

    public TimeSpan? ExpectedDuration(JsonObject input) => DesktopNodes.WaitSpan(input);

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is { } reason)
        {
            return NodeResult.Fail(reason, retryable: false);
        }

        var query = DesktopNodes.Window(context);
        DesktopWindow? window;
        try
        {
            window = await DesktopNodes.WaitWindowAsync(desktop, query, DesktopNodes.WaitSpan(context.Input), cancellationToken);
        }
        catch (DesktopAmbiguousException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }
        catch (DesktopException ex)
        {
            return NodeResult.Fail($"Windows не дал найти окно: {ex.Message}");
        }

        if (window is null)
        {
            return NodeResult.Ok(Ports.Timeout, $"Окно ({query}) не найдено.");
        }

        var current = window.Info;
        try
        {
            if (context.GetBoolean("activate", true))
            {
                current = await desktop.ActivateAsync(window, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is DesktopException or DesktopInputBlockedException)
        {
            return NodeResult.Fail($"Окно «{window.Info.Title}» найдено, но не вышло на передний план: {ex.Message}");
        }

        var info = Describe(current);
        var updates = new Dictionary<string, JsonNode?>();
        if (context.GetString("saveTo") is { Length: > 0 } saveTo)
        {
            updates[saveTo] = info.DeepClone();
        }

        return NodeResult.Ok(Ports.Out, updates, info, $"Окно «{window.Info.Title}» ({window.Info.ProcessName}).");
    }

    internal static JsonObject Describe(WindowInfo info) => new()
    {
        ["title"] = info.Title,
        ["process"] = info.ProcessName,
        ["processId"] = info.ProcessId,
        ["x"] = info.Bounds.X,
        ["y"] = info.Bounds.Y,
        ["width"] = info.Bounds.Width,
        ["height"] = info.Bounds.Height,
    };
}

// ───────────── Нажать в окне ─────────────

public sealed class ClickElementNode(IDesktop desktop, ISnapshotStore? snapshots = null) : INodeExecutor
{
    public const string TypeName = "desktop.click";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Нажать в окне",
        Description = "Нажимает кнопку, пункт меню, флажок или другой элемент окна программы. Если итог неизвестен (сбой во время " +
                      "нажатия), Pepa не нажимает второй раз сама — решение за вами.",
        Icon = "mouse-pointer-click",
        Effect = NodeEffect.External,
        SupportsVerification = false,
        SummaryField = "name",
        ResourceHint = DesktopNodes.InputHint,
        Outputs = [DesktopNodes.Out, DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField,
            DesktopNodes.ProcessField,
            .. DesktopNodes.ElementFields(),
            new FieldDescriptor
            {
                Name = "how", Label = "Как нажимать", Kind = FieldKind.Select, Default = "auto",
                Options =
                [
                    new("auto", "Без мыши, если элемент умеет (иначе мышью)"), new("mouse", "Мышью"),
                    new("double", "Мышью, двойной щелчок"), new("right", "Правой кнопкой мыши"),
                ],
                Help = "«Без мыши» надёжнее: работает, даже если окно перекрыто. Некоторые программы реагируют только на мышь.",
            },
            DesktopNodes.WaitField(),
        ],
    };

    public ResourceRequirement? RequiredResource(JsonObject input) => DesktopNodes.InputResource;

    public TimeSpan? ExpectedDuration(JsonObject input) => DesktopNodes.WaitSpan(input) * 2;

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        DesktopNodes.RunAsync(desktop, snapshots, context, needElement: true, "нажать", async (window, element) =>
        {
            var how = context.GetString("how") ?? "auto";
            var label = Label(element!);
            // Окна программы до нажатия: если она «зависнет» на нажатии, новое окно (диалог) подтвердит, что нажатие прошло.
            var before = how == "auto" ? await WindowsOfAsync(window.Info.ProcessId, cancellationToken) : null;
            var invoked = how == "auto" ? await desktop.InvokeAsync(element!, cancellationToken) : InvokeOutcome.NotSupported;
            switch (invoked)
            {
                case InvokeOutcome.Done:
                    return NodeResult.Ok(Ports.Out, $"Нажато (без мыши): {label} в «{window.Info.Title}».");
                case InvokeOutcome.Pending:
                {
                    var after = await WindowsOfAsync(window.Info.ProcessId, cancellationToken);
                    var dialog = before is null ? null : after?.FirstOrDefault(w => before.All(b => b.Handle != w.Handle));
                    return dialog is not null
                        ? new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), null,
                            $"Нажато (без мыши): {label} в «{window.Info.Title}». Программа открыла окно «{dialog.Title}» и ждёт ответа в нём.",
                            MessageLevel.Warning)
                        : new NodeResult.Unknown(
                            $"Нажатие {label} в «{window.Info.Title}» отправлено, но программа не ответила и нового окна не открыла — " +
                            "неизвестно, сработало ли оно (программа может быть занята). Посмотрите на программу и решите, повторять ли.");
                }
            }

            await desktop.ActivateAsync(window, cancellationToken);
            await desktop.ClickAsync(window, element!, how == "right" ? MouseButtonKind.Right : MouseButtonKind.Left, how == "double", cancellationToken);
            return NodeResult.Ok(Ports.Out, $"Нажато мышью: {label} в «{window.Info.Title}».");
        }, cancellationToken);

    /// <summary>Окна процесса; не удалось перечислить — null (тогда новое окно не подтверждает нажатие).</summary>
    private async Task<IReadOnlyList<WindowInfo>?> WindowsOfAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            return (await desktop.ListWindowsAsync(cancellationToken)).Where(w => w.ProcessId == processId).ToList();
        }
        catch (DesktopException)
        {
            return null;
        }
    }

    internal static string Label(DesktopElement element) =>
        element.Info.Name is { Length: > 0 } name ? $"«{name}» ({element.Info.ControlType})" : $"{element.Info.ControlType} {element.Info.AutomationId}".Trim();
}

// ───────────── Ввести в окне ─────────────

public sealed class FillElementNode(IDesktop desktop, ISnapshotStore? snapshots = null) : INodeExecutor
{
    public const string TypeName = "desktop.fill";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Ввести в окне",
        Description = "Записывает текст в поле окна программы (прежний текст заменяется). После сбоя Pepa проверит, какой текст " +
                      "в поле. Если поле не принимает текст напрямую, Pepa печатает его клавиатурой — многострочный текст так не вводится " +
                      "(Enter мог бы отправить форму).",
        Icon = "text-cursor-input",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "name",
        ResourceHint = DesktopNodes.InputHint,
        Outputs = [DesktopNodes.Out, DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField,
            DesktopNodes.ProcessField,
            .. DesktopNodes.ElementFields("Поле"),
            new FieldDescriptor { Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate, Placeholder = "{{ vars.code }}" },
            new FieldDescriptor
            {
                Name = "mode", Label = "Как вводить", Kind = FieldKind.Select, Default = "auto",
                Options =
                [
                    new("auto", "Без клавиатуры, если поле умеет (иначе клавиатурой)"),
                    new("type", "Клавиатурой, по символам"),
                ],
            },
            DesktopNodes.WaitField(),
        ],
    };

    public ResourceRequirement? RequiredResource(JsonObject input) => DesktopNodes.InputResource;

    public TimeSpan? ExpectedDuration(JsonObject input) =>
        DesktopNodes.WaitSpan(input) * 2 + TimeSpan.FromMilliseconds(60 * (input.GetString("text")?.Length ?? 0));

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        DesktopNodes.RunAsync(desktop, snapshots, context, needElement: true, "ввести текст", async (window, element) =>
        {
            var text = context.GetString("text") ?? "";
            var label = ClickElementNode.Label(element!);
            if (context.GetString("mode") != "type" && await desktop.SetValueAsync(element!, text, cancellationToken))
            {
                return NodeResult.Ok(Ports.Out, $"Записано в {label}: {Preview(text)}");
            }

            if (text.Contains('\n') || text.Contains('\r') || text.Contains('\t'))
            {
                return NodeResult.Fail(
                    $"Поле {label} не принимает текст напрямую, а печатать перевод строки или табуляцию клавиатурой опасно: Enter может " +
                    "отправить форму или сообщение, Tab — перейти к другому полю. Уберите переводы строк из текста или введите его блоком " +
                    "«Клавиатура» (там Enter нажимается осознанно).", retryable: false);
            }

            await desktop.ActivateAsync(window, cancellationToken);
            await desktop.FocusAsync(window, element!, cancellationToken);
            await desktop.PressKeysAsync(window, ["Ctrl", "A"], cancellationToken);
            if (text.Length == 0)
            {
                await desktop.PressKeysAsync(window, ["Delete"], cancellationToken);
            }
            else
            {
                await desktop.TypeTextAsync(window, text, 20, cancellationToken);
            }

            return NodeResult.Ok(Ports.Out, $"Напечатано в {label}: {Preview(text)}");
        }, cancellationToken);

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is not null)
        {
            return VerificationResult.Inconclusive(desktop.UnavailableReason);
        }

        try
        {
            var window = await DesktopNodes.WaitWindowAsync(desktop, DesktopNodes.Window(context), TimeSpan.FromSeconds(3), cancellationToken);
            if (window is null)
            {
                return VerificationResult.Inconclusive("окна нет — проверить нечем");
            }

            var element = await DesktopNodes.WaitElementAsync(desktop, window, DesktopNodes.Element(context), context.GetString("match") ?? "strict",
                TimeSpan.FromSeconds(3), cancellationToken);
            if (element is null)
            {
                return VerificationResult.Inconclusive("поля нет — проверить нечем");
            }

            var value = await desktop.ReadAsync(element, "value", cancellationToken);
            var expected = context.GetString("text") ?? "";
            if (value is not null && value.ReplaceLineEndings("\n") == expected.ReplaceLineEndings("\n"))
            {
                return VerificationResult.Done("в поле уже нужный текст");
            }

            // Многострочный текст поле может хранить по-своему: «другой текст» не доказывает, что ввода не было.
            return expected.Contains('\n') || expected.Contains('\r')
                ? VerificationResult.Inconclusive("текст в поле не совпадает с нужным, но многострочный текст поле могло изменить — посмотрите сами")
                : VerificationResult.NotDone("в поле другой текст — можно ввести заново");
        }
        catch (Exception ex) when (ex is DesktopException or DesktopAmbiguousException)
        {
            return VerificationResult.Inconclusive($"не удалось прочитать поле: {ex.Message}");
        }
    }

    private static string Preview(string text) => text.Length > 40 ? text[..40] + "…" : text;
}

// ───────────── Прочитать из окна ─────────────

public sealed class ReadElementNode(IDesktop desktop, ISnapshotStore? snapshots = null) : INodeExecutor
{
    public const string TypeName = "desktop.read";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Прочитать из окна",
        Description = "Читает текст или значение элемента окна, или проверяет, есть ли элемент. Ничего не меняет.",
        Icon = "scan-search",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "name",
        Outputs = [DesktopNodes.Out, DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField,
            DesktopNodes.ProcessField,
            .. DesktopNodes.ElementFields(),
            new FieldDescriptor
            {
                Name = "what", Label = "Что прочитать", Kind = FieldKind.Select, Default = "text",
                Options = [new("text", "Текст"), new("value", "Значение поля"), new("name", "Подпись"), new("exists", "Есть ли элемент (да/нет)")],
            },
            DesktopNodes.WaitField(10),
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить в", Kind = FieldKind.Variable, Required = true },
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => DesktopNodes.WaitSpan(input) * 2;

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var saveTo = context.RequireString("saveTo");
        var what = context.GetString("what") ?? "text";
        if (what == "exists")
        {
            if (desktop.UnavailableReason is { } reason)
            {
                return NodeResult.Fail(reason, retryable: false);
            }

            try
            {
                var window = await DesktopNodes.WaitWindowAsync(desktop, DesktopNodes.Window(context), DesktopNodes.WaitSpan(context.Input), cancellationToken);
                var element = window is null
                    ? null
                    : await DesktopNodes.WaitElementAsync(desktop, window, DesktopNodes.Element(context), "first", DesktopNodes.WaitSpan(context.Input),
                        cancellationToken);
                return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?> { [saveTo] = element is not null }, null,
                    element is not null ? "Элемент есть." : "Элемента нет.");
            }
            catch (Exception ex) when (ex is DesktopException or DesktopAmbiguousException)
            {
                return NodeResult.Fail($"Не удалось проверить элемент: {ex.Message}");
            }
        }

        return await DesktopNodes.RunAsync(desktop, snapshots, context, needElement: true, "прочитать", async (_, element) =>
        {
            var value = await desktop.ReadAsync(element!, what, cancellationToken) ?? "";
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?> { [saveTo] = value }, null,
                $"Прочитано: {(value.Length > 60 ? value[..60] + "…" : value)}");
        }, cancellationToken);
    }
}

// ───────────── Ждать элемент в окне ─────────────

public sealed class WaitElementNode(IDesktop desktop) : INodeExecutor
{
    public const string TypeName = "desktop.waitFor";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Ждать элемент в окне",
        Description = "Ждёт, пока в окне появится (или исчезнет) элемент — например, сообщение «Готово». Ничего не меняет.",
        Icon = "timer",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "name",
        Outputs = [new PortDescriptor(Ports.Out, "Дождались"), new PortDescriptor(Ports.Timeout, "Не дождались", PortKind.Optional), DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField,
            DesktopNodes.ProcessField,
            .. DesktopNodes.ElementFields(),
            new FieldDescriptor
            {
                Name = "state", Label = "Ждать, пока элемент", Kind = FieldKind.Select, Default = "appear",
                Options = [new("appear", "появится"), new("disappear", "исчезнет")],
            },
            DesktopNodes.WaitField(60) with { Label = "Ждать, секунд" },
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => DesktopNodes.WaitSpan(input) * 2;

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is { } reason)
        {
            return NodeResult.Fail(reason, retryable: false);
        }

        var windowQuery = DesktopNodes.Window(context);
        var elementQuery = DesktopNodes.Element(context);
        var wait = DesktopNodes.WaitSpan(context.Input);
        var disappear = context.GetString("state") == "disappear";
        var deadline = DateTime.UtcNow + wait;
        var windowSeen = false;
        try
        {
            while (true)
            {
                var window = await DesktopNodes.WaitWindowAsync(desktop, windowQuery, TimeSpan.Zero, cancellationToken);
                windowSeen |= window is not null;
                if (disappear && window is null && windowSeen)
                {
                    // Окно было и закрылось — элемента нет вместе с ним. Сказать об этом прямо: окно могло и упасть.
                    return new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), null,
                        $"Окно ({windowQuery}) закрылось — вместе с ним исчез и элемент ({elementQuery}).", MessageLevel.Warning);
                }

                var present = window is not null && (await desktop.FindElementsAsync(window, elementQuery, cancellationToken)).Count > 0;
                // «Исчез» засчитывается, только если окно на месте: иначе (ошибка в заголовке) «исчезновение» было бы мнимым.
                if (present != disappear && (!disappear || window is not null))
                {
                    return NodeResult.Ok(Ports.Out, disappear ? $"Элемент ({elementQuery}) исчез." : $"Элемент ({elementQuery}) появился.");
                }

                if (DateTime.UtcNow >= deadline)
                {
                    if (disappear && !windowSeen)
                    {
                        return NodeResult.Fail($"Окно ({windowQuery}) не найдено — проверить, исчез ли элемент, нельзя. Проверьте заголовок окна.",
                            retryable: false);
                    }

                    return NodeResult.Ok(Ports.Timeout, $"Не дождались за {wait.TotalSeconds:0} с: элемент ({elementQuery}) {(disappear ? "не исчез" : "не появился")}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            }
        }
        catch (DesktopAmbiguousException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }
        catch (DesktopException ex)
        {
            return NodeResult.Fail($"Windows не дал проверить окно: {ex.Message}");
        }
    }
}

// ───────────── Клавиатура ─────────────

public sealed class KeyboardNode(IDesktop desktop, ISnapshotStore? snapshots = null) : INodeExecutor
{
    public const string TypeName = "desktop.keys";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Клавиатура",
        Description = "Печатает текст или нажимает сочетание клавиш в окне программы (сначала выводит окно на передний план). " +
                      "Запасной способ, если элемент нельзя найти: результат не проверяется, после сбоя решение за вами.",
        Icon = "keyboard",
        Effect = NodeEffect.External,
        SupportsVerification = false,
        SummaryField = "keys",
        ResourceHint = DesktopNodes.InputHint,
        Outputs = [DesktopNodes.Out, DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField with { Help = "Окно, куда пойдёт ввод. Pepa не печатает «вслепую» в то окно, что окажется сверху." },
            DesktopNodes.ProcessField,
            new FieldDescriptor
            {
                Name = "mode", Label = "Что сделать", Kind = FieldKind.Select, Default = "keys",
                Options = [new("keys", "Нажать сочетание клавиш"), new("type", "Напечатать текст")],
            },
            new FieldDescriptor
            {
                Name = "keys", Label = "Сочетание", Kind = FieldKind.Text, Required = true, Default = "Enter", Placeholder = "Ctrl+S",
                VisibleWhen = new FieldCondition("mode", ["keys"]),
                Help = "Например: Enter, Tab, Ctrl+V, Alt+F4, Ctrl+Shift+S.",
            },
            new FieldDescriptor
            {
                Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate, Required = true,
                VisibleWhen = new FieldCondition("mode", ["type"]),
                Help = "Перевод строки нажимает Enter, табуляция — Tab (как при обычном наборе).",
            },
            DesktopNodes.WaitField(10) with { Label = "Ждать окно, секунд" },
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
    {
        if (node.Config.GetString("mode") is null or "keys" && node.Config.GetString("keys") is { Length: > 0 } keys)
        {
            string? error = null;
            try
            {
                KeyCombo.Parse(keys);
            }
            catch (NodeInputException ex)
            {
                error = ex.Message;
            }

            if (error is not null)
            {
                yield return new NodeConfigIssue(error, "keys");
            }
        }
    }

    public ResourceRequirement? RequiredResource(JsonObject input) => DesktopNodes.InputResource;

    public TimeSpan? ExpectedDuration(JsonObject input) =>
        DesktopNodes.WaitSpan(input) + TimeSpan.FromMilliseconds(60 * (input.GetString("text")?.Length ?? 0));

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var type = context.GetString("mode") == "type";
        var keys = type ? null : KeyCombo.Parse(context.RequireString("keys"));
        return DesktopNodes.RunAsync(desktop, snapshots, context, needElement: false, type ? "напечатать текст" : "нажать клавиши", async (window, _) =>
        {
            await desktop.ActivateAsync(window, cancellationToken);
            if (type)
            {
                var text = context.GetString("text") ?? "";
                await desktop.TypeTextAsync(window, text, 20, cancellationToken);
                return NodeResult.Ok(Ports.Out, $"Напечатано в «{window.Info.Title}»: {(text.Length > 40 ? text[..40] + "…" : text)}");
            }

            await desktop.PressKeysAsync(window, keys!, cancellationToken);
            return NodeResult.Ok(Ports.Out, $"Нажато {string.Join("+", keys!)} в «{window.Info.Title}».");
        }, cancellationToken);
    }
}

// ───────────── Мышь ─────────────

public sealed class MouseNode(IDesktop desktop, ISnapshotStore? snapshots = null) : INodeExecutor
{
    public const string TypeName = "desktop.mouse";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Мышь: нажать в точке",
        Description = "Нажимает мышью в точке окна. Самый ненадёжный способ: точка зависит от размера окна и экрана. " +
                      "Используйте, только если элемент нельзя найти блоком «Нажать в окне».",
        Icon = "mouse-pointer-click",
        Effect = NodeEffect.External,
        SupportsVerification = false,
        SummaryField = "window",
        ResourceHint = DesktopNodes.InputHint,
        Outputs = [DesktopNodes.Out, DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField with { Required = true, Help = "Координаты отсчитываются от левого верхнего угла этого окна." },
            DesktopNodes.ProcessField,
            new FieldDescriptor { Name = "x", Label = "X от левого края окна", Kind = FieldKind.Number, Required = true, Min = 0, Max = 20000 },
            new FieldDescriptor { Name = "y", Label = "Y от верхнего края окна", Kind = FieldKind.Number, Required = true, Min = 0, Max = 20000 },
            new FieldDescriptor
            {
                Name = "button", Label = "Кнопка", Kind = FieldKind.Select, Default = "left",
                Options = [new("left", "левая"), new("right", "правая"), new("middle", "средняя")],
            },
            new FieldDescriptor { Name = "double", Label = "Двойной щелчок", Kind = FieldKind.Boolean, Default = false },
            DesktopNodes.WaitField(10) with { Label = "Ждать окно, секунд" },
        ],
    };

    public ResourceRequirement? RequiredResource(JsonObject input) => DesktopNodes.InputResource;

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var x = (int)(context.GetNumber("x") ?? throw new NodeInputException("Не задана координата X."));
        var y = (int)(context.GetNumber("y") ?? throw new NodeInputException("Не задана координата Y."));
        return DesktopNodes.RunAsync(desktop, snapshots, context, needElement: false, "нажать мышью", async (window, _) =>
        {
            // Размер и положение — после вывода на передний план: свёрнутое окно до этого «стоит» далеко за краем экрана.
            var bounds = (await desktop.ActivateAsync(window, cancellationToken)).Bounds;
            if (x >= bounds.Width || y >= bounds.Height)
            {
                return NodeResult.Fail($"Точка ({x}, {y}) за пределами окна «{window.Info.Title}» размером {bounds.Width}×{bounds.Height} — ничего не нажато.",
                    retryable: false);
            }

            var button = context.GetString("button") switch
            {
                "right" => MouseButtonKind.Right,
                "middle" => MouseButtonKind.Middle,
                _ => MouseButtonKind.Left,
            };
            await desktop.MouseClickAsync(window, bounds.X + x, bounds.Y + y, button, context.GetBoolean("double"), cancellationToken);
            return NodeResult.Ok(Ports.Out, $"Нажато мышью в ({x}, {y}) окна «{window.Info.Title}».");
        }, cancellationToken);
    }
}

// ───────────── Закрыть окно ─────────────

public sealed class CloseWindowNode(IDesktop desktop) : INodeExecutor
{
    public const string TypeName = "desktop.close";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = DesktopNodes.Category.Id,
        Title = "Закрыть окно",
        Description = "Закрывает окно, как крестиком. Если программа спросит «Сохранить?», окно останется — блок сообщит об этом.",
        Icon = "circle-x",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "window",
        Outputs = [DesktopNodes.Out, DesktopNodes.Error],
        Fields =
        [
            DesktopNodes.WindowField with { Required = true },
            DesktopNodes.ProcessField,
            DesktopNodes.WaitField(10) with { Label = "Ждать закрытия, секунд" },
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => DesktopNodes.WaitSpan(input) * 2;

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is { } reason)
        {
            return NodeResult.Fail(reason, retryable: false);
        }

        var query = DesktopNodes.Window(context);
        DesktopWindow? window;
        try
        {
            window = await DesktopNodes.WaitWindowAsync(desktop, query, TimeSpan.Zero, cancellationToken);
        }
        catch (DesktopAmbiguousException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }
        catch (DesktopException ex)
        {
            return NodeResult.Fail($"Windows не дал найти окно: {ex.Message}");
        }

        if (window is null)
        {
            // Окна нет с самого начала: возможно, ошибка в заголовке. Успехом это не считается.
            return NodeResult.Fail($"Окно ({query}) не найдено — закрывать нечего. Проверьте заголовок (или окно уже было закрыто).", retryable: false);
        }

        try
        {
            await desktop.CloseWindowAsync(window, cancellationToken);
            var deadline = DateTime.UtcNow + DesktopNodes.WaitSpan(context.Input);
            while (await desktop.IsOpenAsync(window, cancellationToken))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return NodeResult.Fail($"Pepa попросила окно «{window.Info.Title}» закрыться, но оно осталось открытым — вероятно, программа " +
                                           "спрашивает «Сохранить?». Ответьте на вопрос сами (или добавьте блок «Нажать в окне»), затем решите, как продолжить.",
                        retryable: false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (DesktopException ex)
        {
            return new NodeResult.Unknown($"Ошибка Windows при закрытии окна «{window.Info.Title}»: {ex.Message}");
        }

        return NodeResult.Ok(Ports.Out, $"Окно «{window.Info.Title}» закрыто.");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is not null)
        {
            return VerificationResult.Inconclusive(desktop.UnavailableReason);
        }

        try
        {
            var windows = await desktop.FindWindowsAsync(DesktopNodes.Window(context), cancellationToken);
            return windows.Count == 0
                ? VerificationResult.Done("окна уже нет")
                : VerificationResult.NotDone("окно ещё открыто — можно закрыть снова");
        }
        catch (DesktopException ex)
        {
            return VerificationResult.Inconclusive($"не удалось проверить окна: {ex.Message}");
        }
    }
}
