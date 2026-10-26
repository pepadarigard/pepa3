using System.Text.Json.Nodes;

namespace Pepa.Adapters.Desktop;

// ───────────── Что видит перехватчик ввода ─────────────

/// <summary>Окно и элемент, на которые пришлось действие пользователя (в момент действия).</summary>
public sealed record DesktopTarget
{
    /// <summary>Окно верхнего уровня, в котором элемент.</summary>
    public required WindowInfo Window { get; init; }

    /// <summary>null — элемент не определился (в точке нет элемента или UI Automation не ответила).</summary>
    public ElementInfo? Element { get; init; }

    /// <summary>Поле пароля: его значение не читается и не записывается.</summary>
    public bool IsPassword { get; init; }

    /// <summary>Сколько элементов в окне с таким же кодом (AutomationId); 0 — кода нет.</summary>
    public int SameId { get; init; }

    /// <summary>Сколько элементов в окне с такой же подписью и типом; 0 — подписи нет.</summary>
    public int SameName { get; init; }

    /// <summary>Пояснение перехватчика (например, «окно под мышью сменилось во время нажатия»).</summary>
    public string? Note { get; init; }
}

public abstract record DesktopInput(DateTimeOffset At);

/// <summary>Нажатие мышью: кнопка нажата и отпущена почти в одной точке. Координаты — экранные.</summary>
public sealed record DesktopClickInput(DateTimeOffset At, DesktopTarget Target, int X, int Y, MouseButtonKind Button) : DesktopInput(At);

/// <summary>Ввод текста в поле закончился: значение поля в этот момент (null — прочитать не удалось или это пароль).</summary>
public sealed record DesktopTextInput(DateTimeOffset At, DesktopTarget Target, string? Text) : DesktopInput(At);

/// <summary>Клавиша или сочетание (не набор текста) — в формате блока «Клавиатура»: «Enter», «Ctrl+S».</summary>
public sealed record DesktopKeyInput(DateTimeOffset At, DesktopTarget Target, string Keys) : DesktopInput(At);

/// <summary>Действие, которое не записывается (перетаскивание, Win+…): пояснение попадёт к следующему шагу.</summary>
public sealed record DesktopSkippedInput(DateTimeOffset At, string Reason) : DesktopInput(At);

/// <summary>Перехват мыши и клавиатуры для записи действий в окнах Windows.</summary>
public interface IDesktopInputMonitor
{
    /// <summary>null — работает; иначе почему нет.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Начать перехват. Действия передаются в <paramref name="sink"/> строго по порядку, из одного потока перехватчика.
    /// Ввод в окна самой Pepa и ввод, сделанный программами (в том числе блоками Pepa), не передаётся.
    /// Не удалось начать — <see cref="DesktopException"/>.
    /// </summary>
    IDesktopInputWatch Start(Action<DesktopInput> sink);
}

public interface IDesktopInputWatch
{
    /// <summary>Остановить перехват; незаконченный ввод текста передаётся до возврата.</summary>
    Task StopAsync();
}

/// <summary>Pepa запущена не в Windows: запись честно сообщает об этом.</summary>
public sealed class UnavailableInputMonitor(string reason) : IDesktopInputMonitor
{
    public string? UnavailableReason { get; } = reason;

    public IDesktopInputWatch Start(Action<DesktopInput> sink) => throw new DesktopException(UnavailableReason!);
}

// ───────────── Клавиши ─────────────

public enum KeyRole
{
    /// <summary>Часть набора текста в поле: записывается итоговым текстом поля, а не нажатиями.</summary>
    Typing,

    /// <summary>Отдельный шаг «Клавиатура».</summary>
    Key,

    /// <summary>Не записывается.</summary>
    Ignore,
}

public readonly record struct KeyDecision(KeyRole Role, string? Keys = null, string? Note = null);

/// <summary>
/// Какие нажатия при записи — набор текста, какие — отдельные клавиши. Набор текста записывается итоговым значением поля
/// (так исправления, вставка и выделение не превращаются в десятки шагов), всё остальное — сочетанием клавиш.
/// </summary>
public static class DesktopKeyRules
{
    private const int Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Alt = 0x12, Escape = 0x1B;

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    /// <summary>Клавиша печатает символ: буквы, цифры, пробел, знаки, цифровая клавиатура.</summary>
    public static bool IsPrintable(int vk) =>
        vk is 0x20 or (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2;

    private static bool IsEditing(int vk) => vk is Backspace or 0x2E or 0x2D or (>= 0x21 and <= 0x28);

    /// <summary>Название клавиши, как в блоке «Клавиатура»; null — такой клавиши в блоке нет (знаки препинания и т.п.).</summary>
    public static string? KeyName(int vk) => vk switch
    {
        Backspace => "Backspace",
        Tab => "Tab",
        Enter => "Enter",
        Escape => "Escape",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrintScreen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5D => "Apps",
        Alt => "Alt",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => ((char)('0' + vk - 0x60)).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
        _ => null,
    };

    /// <summary>
    /// Что записать для нажатой клавиши. Клавиши-модификаторы сами по себе не записываются (кроме отдельного нажатия Alt —
    /// перехватчик сообщает о нём, когда Alt отпущен, а других клавиш не нажимали): они входят в сочетания.
    /// </summary>
    /// <param name="editable">Фокус в поле ввода.</param>
    /// <param name="multiLine">Поле многострочное: Enter и Tab в нём — часть текста.</param>
    public static KeyDecision Classify(int vk, bool ctrl, bool alt, bool shift, bool win, bool editable, bool multiLine)
    {
        if (vk == Alt && !ctrl && !shift && !win)
        {
            return new KeyDecision(KeyRole.Key, "Alt");
        }

        if (IsModifier(vk))
        {
            return new KeyDecision(KeyRole.Ignore);
        }

        if (win)
        {
            return new KeyDecision(KeyRole.Ignore, Note: "сочетания с клавишей Win не записываются");
        }

        if (alt && !ctrl && vk is Tab or Escape)
        {
            // Переключение окон: следующий блок сам найдёт своё окно.
            return new KeyDecision(KeyRole.Ignore);
        }

        // Правый Alt (AltGr) Windows передаёт как Ctrl+Alt — им печатают символы (@, € и т.п.).
        var plain = (!ctrl && !alt) || (ctrl && alt);
        if (editable)
        {
            if (plain && (IsPrintable(vk) || IsEditing(vk)))
            {
                return new KeyDecision(KeyRole.Typing);
            }

            if (!ctrl && !alt && vk is Enter or Tab && multiLine)
            {
                return new KeyDecision(KeyRole.Typing);
            }

            // Выделить всё, копировать, вставить, вырезать, отменить, повторить, удаление и переход по словам.
            if (ctrl && !alt && vk is 0x41 or 0x43 or 0x56 or 0x58 or 0x59 or 0x5A or Backspace or 0x2E or (>= 0x23 and <= 0x28))
            {
                return new KeyDecision(KeyRole.Typing);
            }
        }

        var name = KeyName(vk);
        if (name is null)
        {
            return new KeyDecision(KeyRole.Ignore, Note: IsPrintable(vk)
                ? "символ, набранный вне поля ввода, не записан"
                : "клавиша не записана: её нет в блоке «Клавиатура»");
        }

        var combo = string.Concat(ctrl ? "Ctrl+" : "", alt ? "Alt+" : "", shift ? "Shift+" : "", name);
        return new KeyDecision(KeyRole.Key, combo);
    }
}

// ───────────── Запись ─────────────

/// <summary>Записанный шаг — готовый блок окон Windows.</summary>
public sealed record RecordedDesktopStep
{
    /// <summary>Тип блока: desktop.click, desktop.fill, desktop.keys, desktop.mouse.</summary>
    public required string Type { get; init; }

    public required JsonObject Config { get; init; }

    /// <summary>Подпись блока на схеме.</summary>
    public string? Label { get; init; }

    /// <summary>Что проверить в этом шаге (ненадёжный поиск, сокращённый заголовок, пропущенные действия…).</summary>
    public string? Note { get; init; }

    /// <summary>Введён пароль: текст не записан, в блоке — переменная <c>password</c>.</summary>
    public bool Secret { get; init; }
}

public sealed record DesktopRecorderState(bool Recording, IReadOnlyList<RecordedDesktopStep> Steps);

public sealed class DesktopRecorderException(string message) : Exception(message);

/// <summary>
/// Запись действий пользователя в окнах Windows: перехватчик сообщает нажатия, набранный текст и клавиши, а здесь они
/// становятся блоками «Нажать в окне», «Ввести в окне», «Клавиатура» и (если элемент не определить) «Мышь: нажать в точке».
/// Элемент описывается так, чтобы блок нашёл его однозначно (уникальный код, иначе подпись и тип); если однозначно нельзя —
/// у шага пояснение, а при выполнении блок остановится, а не нажмёт наугад.
/// </summary>
public sealed class DesktopRecorder(IDesktopInputMonitor monitor)
{
    /// <summary>Двойной щелчок: второе нажатие не позже и не дальше этого.</summary>
    private static readonly TimeSpan DoubleClickTime = TimeSpan.FromMilliseconds(500);
    private const int DoubleClickDistance = 4;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _notedWindows = new(StringComparer.Ordinal);
    private IDesktopInputWatch? _watch;
    private bool _active;
    private string? _pendingNote;

    /// <summary>Записан шаг или запись началась/закончилась.</summary>
    public event Action? Changed;

    public string? UnavailableReason => monitor.UnavailableReason;

    public DesktopRecorderState State
    {
        get
        {
            lock (_gate)
            {
                return new DesktopRecorderState(_active, _entries.Select(e => e.Step).ToList());
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_watch is not null)
            {
                throw new DesktopRecorderException("Запись уже идёт. Остановите её, прежде чем начать новую.");
            }

            lock (_gate)
            {
                _entries.Clear();
                _notedWindows.Clear();
                _pendingNote = null;
                _active = true;
            }

            try
            {
                _watch = monitor.Start(Accept);
            }
            catch (DesktopException ex)
            {
                lock (_gate)
                {
                    _active = false;
                }

                throw new DesktopRecorderException(ex.Message);
            }
        }
        finally
        {
            _lock.Release();
        }

        Raise();
    }

    public async Task<IReadOnlyList<RecordedDesktopStep>> StopAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_watch is { } watch)
            {
                // Перехватчик передаёт незаконченный ввод текста до возврата — только потом запись закрывается.
                await watch.StopAsync();
            }
        }
        finally
        {
            // Даже если перехватчик не ответил, запись закрыта: иначе новую нельзя было бы начать.
            _watch = null;
            lock (_gate)
            {
                _active = false;
                if (_pendingNote is not null && _entries.Count > 0)
                {
                    var last = _entries[^1];
                    _entries[^1] = last with { Step = last.Step with { Note = Join(last.Step.Note, $"после этого шага: {_pendingNote}") } };
                }

                _pendingNote = null;
            }

            _lock.Release();
        }

        Raise();
        return State.Steps;
    }

    /// <summary>Действие от перехватчика (по одному, по порядку).</summary>
    internal void Accept(DesktopInput input)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            switch (input)
            {
                case DesktopSkippedInput skipped:
                    if (_pendingNote is null || !_pendingNote.Contains(skipped.Reason, StringComparison.Ordinal))
                    {
                        _pendingNote = Join(_pendingNote, skipped.Reason);
                    }

                    break;
                case DesktopClickInput click:
                    OnClick(click);
                    break;
                case DesktopTextInput text:
                    OnText(text);
                    break;
                case DesktopKeyInput key:
                    Add(new Entry(Step(key.Target, "desktop.keys", new JsonObject { ["mode"] = "keys", ["keys"] = key.Keys }, $"Клавиши {key.Keys}"),
                        EntryKind.Keys, key.Target, key.At, key.Keys));
                    break;
            }
        }

        Raise();
    }

    private void OnClick(DesktopClickInput click)
    {
        if (click.Button == MouseButtonKind.Left && _entries.Count > 0 && _entries[^1] is { Kind: EntryKind.Click, Double: false, Right: false } previous &&
            click.At - previous.At <= DoubleClickTime && Math.Abs(click.X - previous.X) <= DoubleClickDistance &&
            Math.Abs(click.Y - previous.Y) <= DoubleClickDistance && SameElement(previous.Target, click.Target))
        {
            // Второе нажатие двойного щелчка — тот же шаг, но двойным щелчком.
            var config = (JsonObject)previous.Step.Config.DeepClone();
            if (previous.Step.Type == "desktop.mouse")
            {
                config["double"] = true;
            }
            else
            {
                config["how"] = "double";
            }

            _entries[^1] = previous with { Step = previous.Step with { Config = config }, Double = true, At = click.At };
            return;
        }

        var target = click.Target;
        var element = Describe(target);
        if (element is null || click.Button == MouseButtonKind.Middle)
        {
            var window = target.Window.Bounds;
            var config = new JsonObject
            {
                ["x"] = Math.Max(0, click.X - window.X),
                ["y"] = Math.Max(0, click.Y - window.Y),
                ["button"] = click.Button switch { MouseButtonKind.Right => "right", MouseButtonKind.Middle => "middle", _ => "left" },
                ["double"] = false,
            };
            var note = element is null
                ? "элемент не определился — нажатие в точке окна: если окно изменит размер или вид, нажатие попадёт не туда"
                : "нажатие средней кнопкой — в точке окна";
            Add(new Entry(Step(target, "desktop.mouse", config, null, note), EntryKind.Click, target, click.At, X: click.X, Y: click.Y));
            return;
        }

        var how = click.Button == MouseButtonKind.Right ? "right" : "auto";
        var clickConfig = element.Value.Config;
        clickConfig["how"] = how;
        var label = target.Element!.Name.Length > 0
            ? $"{(how == "right" ? "Правой кнопкой" : "Нажать")} «{Short(target.Element.Name)}»"
            : null;
        Add(new Entry(Step(target, "desktop.click", clickConfig, label, element.Value.Note), EntryKind.Click, target, click.At,
            X: click.X, Y: click.Y, Right: how == "right"));
    }

    private void OnText(DesktopTextInput input)
    {
        var target = input.Target;
        var secret = target.IsPassword;
        var text = secret ? "{{ vars.password }}" : input.Text ?? "";
        var unread = !secret && input.Text is null ? "Pepa не смогла прочитать текст поля — впишите его" : null;
        var element = Describe(target);

        // Набор продолжился после Enter или Tab в том же поле, и они стали частью текста (многострочное поле, о котором
        // Windows этого не сообщает) — итоговый текст включает и их. Если не стали (Enter отправил поиск) — шаги остаются.
        while (!secret && _entries.Count >= 2 && _entries[^1] is { Kind: EntryKind.Keys, Keys: "Enter" or "Tab" or "Shift+Enter" } key &&
               SameElement(key.Target, target) && _entries[^2] is { Kind: EntryKind.Text, Text: { } previousText } before &&
               SameElement(before.Target, target) && Count(text, key.Keys == "Tab" ? '\t' : '\n') > Count(previousText, key.Keys == "Tab" ? '\t' : '\n'))
        {
            _entries.RemoveRange(_entries.Count - 2, 2);
        }

        string? carried = null;
        if (_entries.Count > 0 && SameElement(_entries[^1].Target, target) &&
            _entries[^1] is { Kind: EntryKind.Text } or { Kind: EntryKind.Click, Double: false, Right: false, Step.Type: "desktop.click" })
        {
            // Повторный ввод в то же поле или нажатие в поле перед вводом: «Ввести в окне» само ставит курсор в поле.
            carried = _entries[^1].Step.Note;
            _entries.RemoveAt(_entries.Count - 1);
        }

        RecordedDesktopStep step;
        if (element is null)
        {
            // Поле не найти ни по подписи, ни по коду — напечатать текст туда, где стоит курсор.
            step = Step(target, "desktop.keys", new JsonObject { ["mode"] = "type", ["text"] = text }, secret ? "Напечатать пароль" : "Напечатать текст",
                Join(carried, Join("поле без подписи и кода: текст печатается туда, где стоит курсор (поставьте его предыдущим шагом); прежний текст поля не стирается", unread)));
        }
        else
        {
            var config = element.Value.Config;
            config["text"] = text;
            config["mode"] = "auto";
            var label = secret ? "Ввести пароль" : target.Element!.Name.Length > 0 ? $"Ввести «{Short(target.Element.Name)}»" : "Ввести текст";
            step = Step(target, "desktop.fill", config, label, Join(carried, Join(element.Value.Note, unread)));
        }

        Add(new Entry(step with { Secret = secret }, EntryKind.Text, target, input.At, Text: secret ? null : input.Text));
    }

    /// <summary>Поля поиска элемента для блока: однозначно, если можно; иначе — с пояснением.</summary>
    private static (JsonObject Config, string? Note)? Describe(DesktopTarget target)
    {
        if (target.Element is not { } e || (e.Name.Length == 0 && e.AutomationId.Length == 0))
        {
            return null;
        }

        var config = new JsonObject
        {
            ["name"] = e.Name,
            ["nameMatch"] = "exact",
            ["automationId"] = "",
            ["controlType"] = DesktopNodes.ControlTypes.Any(t => t.Value == e.ControlType) ? e.ControlType : "",
            ["match"] = "strict",
        };
        string? note = null;
        if (e.AutomationId.Length > 0 && target.SameId == 1)
        {
            // Уникальный код — надёжный поиск; подпись остаётся проверкой, что это тот же элемент.
            config["automationId"] = e.AutomationId;
        }
        else if (e.Name.Length > 0)
        {
            if (target.SameName > 1)
            {
                note = $"в окне {target.SameName} элемента с такой подписью — при выполнении блок остановится; уточните элемент (кнопка «Выбрать элемент в окне»)";
            }
        }
        else
        {
            config["automationId"] = e.AutomationId;
            if (target.SameId > 1)
            {
                note = $"в окне {target.SameId} элемента с таким кодом — при выполнении блок остановится; уточните элемент (кнопка «Выбрать элемент в окне»)";
            }
        }

        if (!e.Enabled)
        {
            note = Join(note, "при записи элемент был недоступен (серый) — при выполнении блок остановится, если он так и останется");
        }

        return (config, note);
    }

    private RecordedDesktopStep Step(DesktopTarget target, string type, JsonObject config, string? label, string? note = null)
    {
        var (title, shortened) = TitlePart(target.Window.Title);
        var fields = new JsonObject { ["window"] = title, ["process"] = target.Window.ProcessName };
        foreach (var (key, value) in config)
        {
            fields[key] = value?.DeepClone();
        }

        var windowKey = $"{target.Window.ProcessName}\n{target.Window.Title}";
        if (_notedWindows.Add(windowKey))
        {
            if (shortened)
            {
                note = Join(note, $"заголовок окна «{target.Window.Title}» сокращён до «{title}»: остальная часть (обычно имя документа) может меняться");
            }
            else if (title.Length == 0)
            {
                note = Join(note, "у окна нет заголовка (меню или всплывающий список) — если блок не найдёт окно однозначно, используйте клавиши");
            }
        }

        if (target.Note is not null)
        {
            note = Join(note, target.Note);
        }

        if (_pendingNote is not null)
        {
            note = Join($"перед этим шагом: {_pendingNote}", note);
            _pendingNote = null;
        }

        return new RecordedDesktopStep { Type = type, Config = fields, Label = label, Note = note };
    }

    /// <summary>
    /// Часть заголовка для поиска окна: у заголовков вида «Документ — Программа» — название программы (имя документа
    /// меняется от запуска к запуску).
    /// </summary>
    internal static (string Title, bool Shortened) TitlePart(string title)
    {
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var at = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && title[(at + separator.Length)..].Trim() is { Length: >= 2 } tail)
            {
                return (tail, true);
            }
        }

        return (title.Trim(), false);
    }

    private void Add(Entry entry) => _entries.Add(entry);

    private static bool SameElement(DesktopTarget a, DesktopTarget b) =>
        a.Window.Handle == b.Window.Handle && a.Element is { } x && b.Element is { } y &&
        x.Name == y.Name && x.ControlType == y.ControlType && x.AutomationId == y.AutomationId && x.Bounds == y.Bounds;

    private static int Count(string text, char c) => text.Count(x => x == c);

    private static string Short(string text) => text.Length > 28 ? text[..28] + "…" : text;

    private static string? Join(string? a, string? b) =>
        string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : $"{a}; {b}";

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

    private enum EntryKind
    {
        Click,
        Text,
        Keys,
    }

    private sealed record Entry(RecordedDesktopStep Step, EntryKind Kind, DesktopTarget Target, DateTimeOffset At, string? Keys = null,
        int X = 0, int Y = 0, bool Double = false, bool Right = false, string? Text = null);
}
