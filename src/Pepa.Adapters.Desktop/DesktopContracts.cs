namespace Pepa.Adapters.Desktop;

public readonly record struct DesktopRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}

/// <summary>Окно верхнего уровня.</summary>
public sealed record WindowInfo(long Handle, string Title, string ProcessName, int ProcessId, DesktopRect Bounds);

/// <summary>Элемент окна (кнопка, поле, пункт списка…).</summary>
public sealed record ElementInfo(string Name, string ControlType, string AutomationId, DesktopRect Bounds, bool Enabled);

/// <summary>Элемент окна в дереве элементов (1 — прямо в окне).</summary>
public sealed record ElementNode(int Depth, ElementInfo Info);

/// <summary>Найденное окно. Реализация хранит свою ссылку на окно; ссылка живёт только в пределах шага.</summary>
public abstract class DesktopWindow
{
    public abstract WindowInfo Info { get; }
}

/// <summary>Найденный элемент окна (только в пределах шага).</summary>
public abstract class DesktopElement
{
    public abstract ElementInfo Info { get; }
}

/// <summary>Какое окно искать: часть заголовка и/или имя программы (без .exe). Регистр не важен.</summary>
public sealed record WindowQuery(string? TitleContains, string? ProcessName)
{
    public override string ToString() =>
        string.Join(", ", new[]
        {
            string.IsNullOrWhiteSpace(TitleContains) ? null : $"заголовок содержит «{TitleContains}»",
            string.IsNullOrWhiteSpace(ProcessName) ? null : $"программа «{ProcessName}»",
        }.Where(s => s is not null));
}

/// <summary>Какой элемент искать внутри окна. Пустые условия не учитываются; нужно хотя бы одно из имени и кода.</summary>
public sealed record ElementQuery
{
    public string? Name { get; init; }

    /// <summary>Имя элемента содержит <see cref="Name"/> (иначе — совпадает целиком). Регистр не важен.</summary>
    public bool NameContains { get; init; }

    public string? AutomationId { get; init; }

    /// <summary>Тип элемента UI Automation: Button, Edit, Text, CheckBox, ListItem, MenuItem… null — любой.</summary>
    public string? ControlType { get; init; }

    public override string ToString() =>
        string.Join(", ", new[]
        {
            string.IsNullOrWhiteSpace(Name) ? null : NameContains ? $"имя содержит «{Name}»" : $"имя «{Name}»",
            string.IsNullOrWhiteSpace(AutomationId) ? null : $"код «{AutomationId}»",
            string.IsNullOrWhiteSpace(ControlType) ? null : $"тип {ControlType}",
        }.Where(s => s is not null));
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
}

/// <summary>Как сработало нажатие без мыши.</summary>
public enum InvokeOutcome
{
    /// <summary>Элемент так нажимать не умеет.</summary>
    NotSupported,

    /// <summary>Нажато, программа ответила.</summary>
    Done,

    /// <summary>
    /// Нажатие передано, но программа ещё не ответила — обычно это значит, что открылось окно-диалог (программа ждёт его
    /// закрытия, прежде чем «закончить» нажатие).
    /// </summary>
    Pending,
}

/// <summary>
/// Окна, элементы, мышь и клавиатура Windows. Методы — одна попытка без ожидания (ждут блоки).
/// Ошибки самого Windows — <see cref="DesktopException"/>. Мышь и клавиатура работают только с окном на переднем плане:
/// если оно не там (или в точке нажатия другое окно), ввод не выполняется — <see cref="DesktopInputBlockedException"/>.
/// </summary>
public interface IDesktop
{
    /// <summary>null — работает; иначе почему нет (например, Pepa запущена не в Windows).</summary>
    string? UnavailableReason { get; }

    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DesktopWindow>> FindWindowsAsync(WindowQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<DesktopElement>> FindElementsAsync(DesktopWindow window, ElementQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Элементы окна деревом (для выбора в конструкторе): по порядку обхода, с глубиной вложенности. Не больше
    /// <paramref name="maxDepth"/> уровней и <paramref name="maxCount"/> элементов. Только чтение.
    /// </summary>
    Task<IReadOnlyList<ElementNode>> DescribeAsync(DesktopWindow window, int maxDepth, int maxCount, CancellationToken cancellationToken);

    /// <summary>Окно ещё открыто.</summary>
    Task<bool> IsOpenAsync(DesktopWindow window, CancellationToken cancellationToken);

    /// <summary>
    /// Вывести окно на передний план (развернуть, если свёрнуто) и убедиться, что оно там. Не удалось (Windows не разрешил,
    /// окно запущено от администратора) — <see cref="DesktopInputBlockedException"/>.
    /// </summary>
    /// <returns>Сведения об окне после активации (положение и размер могли измениться).</returns>
    Task<WindowInfo> ActivateAsync(DesktopWindow window, CancellationToken cancellationToken);

    /// <summary>Нажать элемент без мыши (UI Automation Invoke/Toggle/Select).</summary>
    Task<InvokeOutcome> InvokeAsync(DesktopElement element, CancellationToken cancellationToken);

    /// <summary>
    /// Нажать мышью в элемент: точка берётся заново и проверяется, что в ней именно этот элемент (не перекрыт, не за краем).
    /// </summary>
    Task ClickAsync(DesktopWindow window, DesktopElement element, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken);

    /// <summary>Записать значение поля без клавиатуры (Value pattern). false — поле так не умеет.</summary>
    Task<bool> SetValueAsync(DesktopElement element, string text, CancellationToken cancellationToken);

    /// <summary>Поставить фокус ввода в элемент и убедиться, что он там (иначе — <see cref="DesktopInputBlockedException"/>).</summary>
    Task FocusAsync(DesktopWindow window, DesktopElement element, CancellationToken cancellationToken);

    /// <summary>Прочитать: «name» — подпись, «value» — значение поля, «text» — текст (значение, иначе подпись).</summary>
    Task<string?> ReadAsync(DesktopElement element, string what, CancellationToken cancellationToken);

    /// <summary>Нажать мышью в точке экрана — только если в ней окно <paramref name="window"/>.</summary>
    Task MouseClickAsync(DesktopWindow window, int x, int y, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken);

    /// <summary>
    /// Напечатать текст клавиатурой (любые буквы, в том числе русские), с паузой между символами. Перед каждым символом
    /// проверяется, что окно всё ещё на переднем плане.
    /// </summary>
    Task TypeTextAsync(DesktopWindow window, string text, int delayMs, CancellationToken cancellationToken);

    /// <summary>Нажать сочетание: клавиши-модификаторы зажимаются, последняя нажимается (Ctrl+Shift+S, Enter, F5).</summary>
    Task PressKeysAsync(DesktopWindow window, IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <returns>Номер процесса.</returns>
    Task<int> StartProgramAsync(string path, string? arguments, CancellationToken cancellationToken);

    /// <summary>Попросить окно закрыться (как крестик).</summary>
    Task CloseWindowAsync(DesktopWindow window, CancellationToken cancellationToken);
}

/// <summary>
/// Ввод мышью или клавиатурой не выполнен (или прерван), потому что нужное окно не на переднем плане, в точке нажатия
/// другое окно или Windows не пропускает ввод в это окно.
/// </summary>
public sealed class DesktopInputBlockedException(string message, bool partial = false) : Exception(message)
{
    /// <summary>Часть ввода уже ушла в окно (например, половина текста) — итог неизвестен.</summary>
    public bool Partial { get; } = partial;
}

/// <summary>Windows не выполнил действие (элемент недоступен, окно закрылось и т.п.).</summary>
public sealed class DesktopException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Pepa запущена не в Windows: блоки окон честно сообщают об этом.</summary>
public sealed class UnavailableDesktop(string reason) : IDesktop
{
    public string? UnavailableReason { get; } = reason;

    private DesktopException Fail() => new(UnavailableReason!);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<WindowInfo>>(Fail());
    public Task<IReadOnlyList<DesktopWindow>> FindWindowsAsync(WindowQuery query, CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<DesktopWindow>>(Fail());
    public Task<IReadOnlyList<DesktopElement>> FindElementsAsync(DesktopWindow window, ElementQuery query, CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<DesktopElement>>(Fail());
    public Task<IReadOnlyList<ElementNode>> DescribeAsync(DesktopWindow window, int maxDepth, int maxCount, CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<ElementNode>>(Fail());
    public Task<bool> IsOpenAsync(DesktopWindow window, CancellationToken cancellationToken) => Task.FromException<bool>(Fail());
    public Task<WindowInfo> ActivateAsync(DesktopWindow window, CancellationToken cancellationToken) => Task.FromException<WindowInfo>(Fail());
    public Task<InvokeOutcome> InvokeAsync(DesktopElement element, CancellationToken cancellationToken) => Task.FromException<InvokeOutcome>(Fail());
    public Task ClickAsync(DesktopWindow window, DesktopElement element, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken) => Task.FromException(Fail());
    public Task<bool> SetValueAsync(DesktopElement element, string text, CancellationToken cancellationToken) => Task.FromException<bool>(Fail());
    public Task FocusAsync(DesktopWindow window, DesktopElement element, CancellationToken cancellationToken) => Task.FromException(Fail());
    public Task<string?> ReadAsync(DesktopElement element, string what, CancellationToken cancellationToken) => Task.FromException<string?>(Fail());
    public Task MouseClickAsync(DesktopWindow window, int x, int y, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken) => Task.FromException(Fail());
    public Task TypeTextAsync(DesktopWindow window, string text, int delayMs, CancellationToken cancellationToken) => Task.FromException(Fail());
    public Task PressKeysAsync(DesktopWindow window, IReadOnlyList<string> keys, CancellationToken cancellationToken) => Task.FromException(Fail());
    public Task<int> StartProgramAsync(string path, string? arguments, CancellationToken cancellationToken) => Task.FromException<int>(Fail());
    public Task CloseWindowAsync(DesktopWindow window, CancellationToken cancellationToken) => Task.FromException(Fail());
}
