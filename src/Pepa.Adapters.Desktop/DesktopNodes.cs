using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Desktop;

public static class DesktopNodes
{
    public static readonly NodeCategory Category = new("desktop", "Окна Windows", 25);

    /// <summary>Мышь и клавиатура одни на компьютер: действия ими выполняет один процесс за раз.</summary>
    public static readonly ResourceRequirement InputResource = new("desktop:input", "Мышь и клавиатура");

    public const string InputHint =
        "Блок занимает мышь и клавиатуру этого компьютера: другие процессы с такими блоками ждут, пока этот процесс закончит " +
        "(или освободит ресурс «desktop:input» блоком «Освободить ресурс»). Если процесс остановился с ошибкой, мышь и клавиатура " +
        "остаются за ним, пока вы не решите, что делать. Пока процесс работает, не трогайте мышь.";

    /// <param name="snapshots">Куда сохранять снимок окна, когда действие в нём не удалось; null — не снимать.</param>
    public static NodeRegistry RegisterDesktopNodes(this NodeRegistry registry, IDesktop desktop, ISnapshotStore? snapshots = null) => registry
        .RegisterCategory(Category)
        .Register(new StartProgramNode(desktop))
        .Register(new FindWindowNode(desktop))
        .Register(new ClickElementNode(desktop, snapshots))
        .Register(new FillElementNode(desktop, snapshots))
        .Register(new ReadElementNode(desktop, snapshots))
        .Register(new WaitElementNode(desktop))
        .Register(new KeyboardNode(desktop, snapshots))
        .Register(new MouseNode(desktop, snapshots))
        .Register(new CloseWindowNode(desktop));

    // ───────────── Общие поля ─────────────

    internal static readonly FieldDescriptor WindowField = new()
    {
        Name = "window", Label = "Окно (часть заголовка)", Kind = FieldKind.Template, Placeholder = "Блокнот",
        Lookup = "desktop-windows",
        Help = "Часть заголовка окна, без учёта регистра. Можно выбрать из открытых окон.",
    };

    internal static readonly FieldDescriptor ProcessField = new()
    {
        Name = "process", Label = "Программа", Kind = FieldKind.Template, Placeholder = "notepad",
        Help = "Необязательно: имя программы без .exe — если окон с похожим заголовком несколько.",
    };

    internal static IReadOnlyList<FieldDescriptor> ElementFields(string label = "Элемент") =>
    [
        new FieldDescriptor
        {
            Name = "name", Label = $"{label}: подпись", Kind = FieldKind.Template, Placeholder = "Сохранить", Lookup = "desktop-elements",
            Help = "Подпись элемента, как её видит Windows (обычно текст на кнопке или название поля). Можно выбрать из элементов окна.",
        },
        new FieldDescriptor
        {
            Name = "nameMatch", Label = "Подпись", Kind = FieldKind.Select, Default = "exact",
            Options = [new("exact", "совпадает целиком"), new("contains", "содержит")],
        },
        new FieldDescriptor
        {
            Name = "automationId", Label = $"{label}: код (AutomationId)", Kind = FieldKind.Template,
            Help = "Внутренний код элемента — надёжнее подписи, если программа его задаёт.",
        },
        new FieldDescriptor
        {
            Name = "controlType", Label = "Тип элемента", Kind = FieldKind.Select, Default = "",
            Options = [new("", "любой"), .. ControlTypes],
        },
        new FieldDescriptor
        {
            Name = "match", Label = "Если подходит несколько элементов", Kind = FieldKind.Select, Default = "strict",
            Options = [new("strict", "Остановиться с ошибкой"), new("first", "Взять первый"), new("last", "Взять последний")],
        },
    ];

    /// <summary>Типы элементов, которые можно выбрать в блоках (остальные — «любой»).</summary>
    public static readonly IReadOnlyList<SelectOption> ControlTypes =
    [
        new("Button", "кнопка"), new("Edit", "поле ввода"), new("Text", "текст"),
        new("CheckBox", "флажок"), new("RadioButton", "переключатель"), new("ComboBox", "выпадающий список"),
        new("ListItem", "пункт списка"), new("MenuItem", "пункт меню"), new("TabItem", "вкладка"),
        new("Hyperlink", "ссылка"), new("Document", "документ"), new("TreeItem", "элемент дерева"),
    ];

    internal static FieldDescriptor WaitField(int seconds = 30) => new()
    {
        Name = "waitSeconds", Label = "Ждать окно и элемент, секунд", Kind = FieldKind.Seconds, Default = seconds, Min = 1, Max = 600,
    };

    internal static readonly PortDescriptor Out = new(Ports.Out, "Далее");
    internal static readonly PortDescriptor Error = new(Ports.Error, "Ошибка", PortKind.Error);

    internal static TimeSpan WaitSpan(JsonObject input) => TimeSpan.FromSeconds(Math.Clamp(input.GetNumber("waitSeconds") ?? 30, 1, 600));

    // ───────────── Поиск ─────────────

    internal static WindowQuery Window(NodeExecutionContext context)
    {
        var title = context.GetString("window")?.Trim();
        var process = context.GetString("process")?.Trim();
        if (process is { Length: > 0 } && process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            process = process[..^4];
        }

        return string.IsNullOrEmpty(title) && string.IsNullOrEmpty(process)
            ? throw new NodeInputException("Укажите окно: часть заголовка или программу.")
            : new WindowQuery(string.IsNullOrEmpty(title) ? null : title, string.IsNullOrEmpty(process) ? null : process);
    }

    internal static ElementQuery Element(NodeExecutionContext context)
    {
        var name = context.GetString("name")?.Trim();
        var id = context.GetString("automationId")?.Trim();
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id))
        {
            throw new NodeInputException("Укажите элемент: подпись или код (AutomationId).");
        }

        return new ElementQuery
        {
            Name = string.IsNullOrEmpty(name) ? null : name,
            NameContains = context.GetString("nameMatch") == "contains",
            AutomationId = string.IsNullOrEmpty(id) ? null : id,
            ControlType = context.GetString("controlType") is { Length: > 0 } type ? type : null,
        };
    }

    /// <summary>Дождаться ровно одного подходящего окна. Несколько окон — ошибка: какое из них нужно, Pepa не угадывает.</summary>
    /// <remarks>
    /// Ждёт по настоящим часам: окна живут в реальном времени, а не во времени процесса. Сбой Windows при переборе окон
    /// (окно закрылось посреди перебора) — «пока не нашли», а не ошибка; ошибка — только если так и не удалось к сроку.
    /// </remarks>
    /// <param name="filter">Дополнительное условие (например, «окно новое, его не было до запуска программы»).</param>
    internal static async Task<DesktopWindow?> WaitWindowAsync(IDesktop desktop, WindowQuery query, TimeSpan wait, CancellationToken cancellationToken,
        Func<DesktopWindow, bool>? filter = null)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            IReadOnlyList<DesktopWindow> windows;
            try
            {
                windows = await desktop.FindWindowsAsync(query, cancellationToken);
            }
            catch (DesktopException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            if (filter is not null)
            {
                windows = windows.Where(filter).ToList();
            }

            if (windows.Count == 1)
            {
                return windows[0];
            }

            if (windows.Count > 1)
            {
                throw new DesktopAmbiguousException(
                    $"Под описание окна ({query}) подходит несколько окон: {string.Join("; ", windows.Take(5).Select(w => $"«{w.Info.Title}» ({w.Info.ProcessName})"))}. " +
                    "Уточните заголовок или укажите программу.");
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    internal static async Task<DesktopElement?> WaitElementAsync(IDesktop desktop, DesktopWindow window, ElementQuery query, string match,
        TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            IReadOnlyList<DesktopElement> elements;
            try
            {
                elements = await desktop.FindElementsAsync(window, query, cancellationToken);
            }
            catch (DesktopException) when (DateTime.UtcNow < deadline)
            {
                // Окно перерисовывается или занято — попробовать ещё раз.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            if (elements.Count == 1 || (elements.Count > 1 && match is "first" or "last"))
            {
                return match == "last" ? elements[^1] : elements[0];
            }

            if (elements.Count > 1)
            {
                throw new DesktopAmbiguousException(
                    $"Под описание элемента ({query}) подходит {elements.Count} элементов, и Pepa не знает, какой нужен. " +
                    "Уточните подпись, тип или код — или выберите «Взять первый».");
            }

            if (DateTime.UtcNow >= deadline || !await desktop.IsOpenAsync(window, cancellationToken))
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    /// <summary>
    /// Найти окно и элемент (действие ещё не начиналось: любая неудача здесь — подтверждённое «не выполнено»),
    /// затем выполнить действие (ошибка Windows во время действия — итог неизвестен).
    /// </summary>
    /// <remarks>Не удалось, а окно найдено — к итогу добавляется снимок окна (<paramref name="snapshots"/>).</remarks>
    internal static async ValueTask<NodeResult> RunAsync(IDesktop desktop, ISnapshotStore? snapshots, NodeExecutionContext context,
        bool needElement, string what, Func<DesktopWindow, DesktopElement?, Task<NodeResult>> action, CancellationToken cancellationToken)
    {
        if (desktop.UnavailableReason is { } reason)
        {
            return NodeResult.Fail(reason, retryable: false);
        }

        var (result, window) = await RunInWindowAsync(desktop, context, needElement, what, action, cancellationToken);
        return result is NodeResult.Failure or NodeResult.Unknown && window is not null && snapshots is not null
            ? await WithSnapshotAsync(result, desktop, snapshots, context, window, cancellationToken)
            : result;
    }

    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Снимок окна к итогу шага — подсказка: не получился (окно закрылось, свёрнуто, диск) — итог тот же.</summary>
    private static async Task<NodeResult> WithSnapshotAsync(NodeResult result, IDesktop desktop, ISnapshotStore snapshots,
        NodeExecutionContext context, DesktopWindow window, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        var snapshot = new JsonObject { ["window"] = window.Info.Title, ["program"] = window.Info.ProcessName };
        try
        {
            var image = await desktop.CaptureAsync(window, cancellationToken).WaitAsync(SnapshotTimeout, cancellationToken);
            if (image is null)
            {
                snapshot["note"] = "Окно закрыто или свёрнуто — снимка нет.";
            }
            else
            {
                snapshot["image"] = await snapshots.SaveAsync(context.ProcessId, image, ".jpg", cancellationToken);
            }
        }
        catch (Exception)
        {
            // Любая ошибка снимка — итог шага остаётся тем же.
            snapshot["note"] = "Снимок окна не получился.";
        }

        return result with { Snapshot = snapshot };
    }

    private static async Task<(NodeResult Result, DesktopWindow? Window)> RunInWindowAsync(IDesktop desktop, NodeExecutionContext context,
        bool needElement, string what, Func<DesktopWindow, DesktopElement?, Task<NodeResult>> action, CancellationToken cancellationToken)
    {
        var windowQuery = Window(context);
        var elementQuery = needElement ? Element(context) : null;
        var wait = WaitSpan(context.Input);
        DesktopWindow? window = null;
        DesktopElement? element = null;
        try
        {
            window = await WaitWindowAsync(desktop, windowQuery, wait, cancellationToken);
            if (window is null)
            {
                return (NodeResult.Fail($"Окно ({windowQuery}) не найдено за {wait.TotalSeconds:0} с — действие «{what}» не выполнялось."), null);
            }

            if (elementQuery is not null)
            {
                element = await WaitElementAsync(desktop, window, elementQuery, context.GetString("match") ?? "strict", wait, cancellationToken);
                if (element is null)
                {
                    return (NodeResult.Fail($"В окне «{window.Info.Title}» нет элемента ({elementQuery}) — действие «{what}» не выполнялось."), window);
                }

                if (!element.Info.Enabled)
                {
                    return (NodeResult.Fail($"Элемент ({elementQuery}) в окне «{window.Info.Title}» недоступен (серый) — действие «{what}» не выполнялось."),
                        window);
                }
            }
        }
        catch (DesktopAmbiguousException ex)
        {
            return (NodeResult.Fail(ex.Message, retryable: false), window);
        }
        catch (DesktopException ex)
        {
            return (NodeResult.Fail($"Windows не дал найти окно или элемент: {ex.Message}"), window);
        }

        try
        {
            return (await action(window, element), window);
        }
        catch (DesktopInputBlockedException ex) when (!ex.Partial)
        {
            // Проверка перед вводом не пропустила его: ничего не нажато и не напечатано.
            return (NodeResult.Fail($"{ex.Message} Действие «{what}» не выполнялось."), window);
        }
        catch (DesktopInputBlockedException ex)
        {
            return (new NodeResult.Unknown($"Ввод прерван на середине: {ex.Message}"), window);
        }
        catch (DesktopException ex)
        {
            return (new NodeResult.Unknown($"Ошибка Windows во время действия «{what}»: {ex.Message}"), window);
        }
    }
}

/// <summary>Под описание подходит несколько окон или элементов.</summary>
internal sealed class DesktopAmbiguousException(string message) : Exception(message);

/// <summary>Разбор сочетаний клавиш: «Ctrl+Shift+S», «Enter», «Alt+F4».</summary>
public static class KeyCombo
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "Ctrl", ["control"] = "Ctrl", ["ctl"] = "Ctrl", ["shift"] = "Shift", ["alt"] = "Alt", ["win"] = "Win",
        ["windows"] = "Win", ["enter"] = "Enter", ["return"] = "Enter", ["tab"] = "Tab", ["esc"] = "Escape", ["escape"] = "Escape",
        ["space"] = "Space", ["пробел"] = "Space", ["backspace"] = "Backspace", ["delete"] = "Delete", ["del"] = "Delete",
        ["insert"] = "Insert", ["ins"] = "Insert", ["home"] = "Home", ["end"] = "End", ["pageup"] = "PageUp", ["pgup"] = "PageUp",
        ["pagedown"] = "PageDown", ["pgdn"] = "PageDown", ["up"] = "Up", ["arrowup"] = "Up", ["down"] = "Down", ["arrowdown"] = "Down",
        ["left"] = "Left", ["arrowleft"] = "Left", ["right"] = "Right", ["arrowright"] = "Right", ["printscreen"] = "PrintScreen",
        ["apps"] = "Apps", ["menu"] = "Apps",
    };

    public static readonly IReadOnlySet<string> Modifiers = new HashSet<string>(["Ctrl", "Shift", "Alt", "Win"]);

    /// <summary>Нормализованные названия клавиш или <see cref="NodeInputException"/> с понятной причиной.</summary>
    public static IReadOnlyList<string> Parse(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            throw new NodeInputException($"Сочетание «{combo}» записано с ошибкой. Пример: Ctrl+S, Alt+F4, Enter.");
        }

        var keys = new List<string>();
        foreach (var part in parts)
        {
            var key = Normalize(part) ?? throw new NodeInputException(
                $"Клавиша «{part}» неизвестна. Можно: буквы и цифры, F1–F24, Enter, Tab, Escape, Space, Backspace, Delete, стрелки Up/Down/Left/Right, Home, End, PageUp, PageDown и Ctrl, Shift, Alt, Win.");
            keys.Add(key);
        }

        if (keys.Take(keys.Count - 1).Any(k => !Modifiers.Contains(k)))
        {
            throw new NodeInputException($"В «{combo}» зажимать можно только Ctrl, Shift, Alt и Win — обычная клавиша должна быть последней.");
        }

        return keys;
    }

    private static string? Normalize(string part)
    {
        if (Aliases.TryGetValue(part, out var alias))
        {
            return alias;
        }

        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            return part.ToUpperInvariant();
        }

        if (part.Length is >= 2 and <= 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
        {
            return $"F{f}";
        }

        return null;
    }
}
