using System.Collections.Concurrent;

namespace Pepa.Adapters.Desktop.Tests;

/// <summary>Имитация Windows: окна с элементами, журнал действий, сбои по заказу.</summary>
internal sealed class FakeDesktop : IDesktop
{
    private readonly List<FakeWindow> _windows = [];
    private readonly object _gate = new();

    public string? UnavailableReason { get; set; }

    /// <summary>Что сделано (по порядку): «invoke Сохранить», «click Сохранить», «type …», «keys Ctrl+S», «mouse 110,220».</summary>
    public ConcurrentQueue<string> Actions { get; } = new();

    /// <summary>Действие с элементом выполняется, но Windows сообщает об ошибке (итог неизвестен).</summary>
    public bool FailAfterAction { get; set; }

    /// <summary>Windows не даёт вывести окно на передний план (пользователь работает в другом окне).</summary>
    public bool ForegroundRefused { get; set; }

    /// <summary>После стольких напечатанных символов окно теряет передний план (пользователь переключился).</summary>
    public int? ForegroundLostAfterChars { get; set; }

    /// <summary>Нажатие без мыши «зависает»: программа не отвечает (занята или открыла диалог).</summary>
    public bool InvokeHangs { get; set; }

    /// <summary>При «зависшем» нажатии программа открывает окно с таким заголовком (диалог того же процесса).</summary>
    public string? DialogOnHang { get; set; }

    /// <summary>Сколько раз перебор окон ещё упадёт (окно закрылось посреди перебора).</summary>
    public int ListFailures { get; set; }

    public FakeWindow AddWindow(string title, string process = "app", int x = 100, int y = 200, int width = 800, int height = 600, int? processId = null)
    {
        var window = new FakeWindow(new WindowInfo(_windows.Count + 1, title, process, processId ?? 1000 + _windows.Count, new DesktopRect(x, y, width, height)));
        lock (_gate)
        {
            _windows.Add(window);
        }

        return window;
    }

    public FakeWindow FindWindow(string title)
    {
        lock (_gate)
        {
            return _windows.Single(w => w.Info.Title == title);
        }
    }

    public void Close(FakeWindow window)
    {
        lock (_gate)
        {
            window.Open = false;
            _windows.Remove(window);
        }
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WindowInfo>>(_windows.Select(w => w.Info).ToList());
        }
    }

    public Task<IReadOnlyList<DesktopWindow>> FindWindowsAsync(WindowQuery query, CancellationToken cancellationToken)
    {
        Available();
        lock (_gate)
        {
            if (ListFailures > 0)
            {
                ListFailures--;
                throw new DesktopException("элемент недоступен (окно закрылось)");
            }

            return Task.FromResult<IReadOnlyList<DesktopWindow>>(_windows
                .Where(w => (query.TitleContains is null || w.Info.Title.Contains(query.TitleContains, StringComparison.CurrentCultureIgnoreCase)) &&
                            (query.ProcessName is null || string.Equals(w.Info.ProcessName, query.ProcessName, StringComparison.OrdinalIgnoreCase)))
                .Cast<DesktopWindow>().ToList());
        }
    }

    public Task<IReadOnlyList<DesktopElement>> FindElementsAsync(DesktopWindow window, ElementQuery query, CancellationToken cancellationToken)
    {
        var fake = (FakeWindow)window;
        // Тест может добавить элемент в окно, пока движок его ищет.
        lock (fake.Elements)
        {
            return Task.FromResult<IReadOnlyList<DesktopElement>>(fake.Elements
                .Where(e => (query.Name is null || (query.NameContains
                                ? e.Info.Name.Contains(query.Name, StringComparison.CurrentCultureIgnoreCase)
                                : string.Equals(e.Info.Name, query.Name, StringComparison.CurrentCultureIgnoreCase))) &&
                            (query.AutomationId is null || e.Info.AutomationId == query.AutomationId) &&
                            (query.ControlType is null || e.Info.ControlType == query.ControlType))
                .Cast<DesktopElement>().ToList());
        }
    }

    public Task<IReadOnlyList<ElementNode>> DescribeAsync(DesktopWindow window, int maxDepth, int maxCount, CancellationToken cancellationToken)
    {
        var fake = (FakeWindow)window;
        lock (fake.Elements)
        {
            return Task.FromResult<IReadOnlyList<ElementNode>>(fake.Elements.Take(maxCount).Select(e => new ElementNode(1, e.Info)).ToList());
        }
    }

    public Task<bool> IsOpenAsync(DesktopWindow window, CancellationToken cancellationToken) => Task.FromResult(((FakeWindow)window).Open);

    public Task<WindowInfo> ActivateAsync(DesktopWindow window, CancellationToken cancellationToken)
    {
        if (ForegroundRefused)
        {
            throw new DesktopInputBlockedException($"Окно «{window.Info.Title}» не на переднем плане.");
        }

        var fake = (FakeWindow)window;
        Actions.Enqueue($"activate {window.Info.Title}");
        fake.Foreground = true;
        return Task.FromResult(fake.Info with { Bounds = fake.RestoredBounds ?? fake.Info.Bounds });
    }

    public async Task<InvokeOutcome> InvokeAsync(DesktopElement element, CancellationToken cancellationToken)
    {
        var fake = (FakeElement)element;
        if (!fake.CanInvoke)
        {
            return InvokeOutcome.NotSupported;
        }

        Actions.Enqueue($"invoke {fake.Info.Name}");
        fake.Clicks++;
        await AfterAction(true);
        if (InvokeHangs && DialogOnHang is { } title)
        {
            FakeWindow owner;
            lock (_gate)
            {
                owner = _windows.First(w => w.Elements.Contains(fake));
            }

            AddWindow(title, owner.Info.ProcessName, processId: owner.Info.ProcessId);
        }

        return InvokeHangs ? InvokeOutcome.Pending : InvokeOutcome.Done;
    }

    public Task ClickAsync(DesktopWindow window, DesktopElement element, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken)
    {
        var fake = (FakeElement)element;
        RequireForeground(window);
        if (fake.Covered)
        {
            throw new DesktopInputBlockedException($"В точке элемента «{fake.Info.Name}» сейчас другое окно.");
        }

        Actions.Enqueue($"{(doubleClick ? "double" : button == MouseButtonKind.Right ? "right" : "click")} {fake.Info.Name}");
        fake.Clicks++;
        return AfterAction(true);
    }

    public Task<bool> SetValueAsync(DesktopElement element, string text, CancellationToken cancellationToken)
    {
        var fake = (FakeElement)element;
        if (!fake.HasValue)
        {
            return Task.FromResult(false);
        }

        fake.Value = text;
        Actions.Enqueue($"set {fake.Info.Name}={text}");
        return AfterAction(true);
    }

    public Task FocusAsync(DesktopWindow window, DesktopElement element, CancellationToken cancellationToken)
    {
        RequireForeground(window);
        Actions.Enqueue($"focus {element.Info.Name}");
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(DesktopElement element, string what, CancellationToken cancellationToken)
    {
        var fake = (FakeElement)element;
        return Task.FromResult(what switch
        {
            "name" => fake.Info.Name,
            "value" => fake.Value,
            _ => fake.Value is { Length: > 0 } v ? v : fake.Info.Name,
        });
    }

    public Task MouseClickAsync(DesktopWindow window, int x, int y, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken)
    {
        RequireForeground(window);
        Actions.Enqueue($"mouse {x},{y}{(doubleClick ? " double" : "")}{(button == MouseButtonKind.Right ? " right" : "")}");
        return AfterAction(true);
    }

    public Task TypeTextAsync(DesktopWindow window, string text, int delayMs, CancellationToken cancellationToken)
    {
        RequireForeground(window);
        if (ForegroundLostAfterChars is { } lost && lost < text.Length)
        {
            Actions.Enqueue($"type {text[..lost]}");
            throw new DesktopInputBlockedException("Окно ушло с переднего плана.", partial: lost > 0);
        }

        Actions.Enqueue($"type {text}");
        return AfterAction(true);
    }

    public Task PressKeysAsync(DesktopWindow window, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        RequireForeground(window);
        Actions.Enqueue($"keys {string.Join("+", keys)}");
        return Task.CompletedTask;
    }

    /// <summary>Как настоящая реализация: ввод только в окно, выведенное на передний план.</summary>
    private static void RequireForeground(DesktopWindow window)
    {
        if (!((FakeWindow)window).Foreground)
        {
            throw new DesktopInputBlockedException($"Окно «{window.Info.Title}» не на переднем плане.");
        }
    }

    public List<string> Started { get; } = [];

    /// <summary>Какое окно открывает программа при запуске (путь → заголовок).</summary>
    public Dictionary<string, string> OpensWindow { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<int> StartProgramAsync(string path, string? arguments, CancellationToken cancellationToken)
    {
        if (path.Contains("нет", StringComparison.Ordinal))
        {
            throw new DesktopException("файл не найден");
        }

        Started.Add(path);
        if (OpensWindow.TryGetValue(path, out var title))
        {
            AddWindow(title, path);
        }

        return Task.FromResult(4242);
    }

    public Task CloseWindowAsync(DesktopWindow window, CancellationToken cancellationToken)
    {
        var fake = (FakeWindow)window;
        Actions.Enqueue($"close {fake.Info.Title}");
        if (!fake.RefusesToClose)
        {
            Close(fake);
        }

        return Task.CompletedTask;
    }

    private void Available()
    {
        if (UnavailableReason is { } reason)
        {
            throw new DesktopException(reason);
        }
    }

    private Task<bool> AfterAction(bool result) =>
        FailAfterAction ? Task.FromException<bool>(new DesktopException("элемент перестал отвечать")) : Task.FromResult(result);
}

internal sealed class FakeWindow(WindowInfo info) : DesktopWindow
{
    public override WindowInfo Info { get; } = info;
    public List<FakeElement> Elements { get; } = [];
    public bool Open { get; set; } = true;
    public bool RefusesToClose { get; set; }

    /// <summary>Выведено на передний план (после «активировать»).</summary>
    public bool Foreground { get; set; }

    /// <summary>Положение после вывода на передний план (у свёрнутого окна оно другое).</summary>
    public DesktopRect? RestoredBounds { get; set; }

    public FakeElement Add(string name, string type = "Button", string id = "", bool enabled = true, bool canInvoke = true, bool hasValue = false)
    {
        var element = new FakeElement(new ElementInfo(name, type, id, new DesktopRect(Info.Bounds.X + 10, Info.Bounds.Y + 10, 80, 30), enabled))
        {
            CanInvoke = canInvoke,
            HasValue = hasValue,
        };
        lock (Elements)
        {
            Elements.Add(element);
        }

        return element;
    }
}

internal sealed class FakeElement(ElementInfo info) : DesktopElement
{
    public override ElementInfo Info { get; } = info;
    public bool CanInvoke { get; init; }
    public bool HasValue { get; init; }
    public string? Value { get; set; }
    public int Clicks { get; set; }

    /// <summary>Элемент перекрыт другим окном или за краем экрана.</summary>
    public bool Covered { get; set; }
}
