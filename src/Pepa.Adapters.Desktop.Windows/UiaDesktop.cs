using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using DrawingPoint = System.Drawing.Point;

namespace Pepa.Adapters.Desktop.Windows;

/// <summary>
/// Окна Windows через UI Automation (FlaUI, UIA3). Все вызовы UI Automation идут по одному в собственном фоновом потоке
/// (так советует Microsoft: не из потока окна, в многопоточном режиме COM). Мышь и клавиатура работают только с окном,
/// которое действительно на переднем плане, — иначе ввод не выполняется. Текст печатается символами Unicode — от
/// раскладки клавиатуры он не зависит.
/// </summary>
public sealed class UiaDesktop : IDesktop, IDisposable
{
    /// <summary>Сколько ждать ответа программы на нажатие без мыши, прежде чем считать, что открылся диалог.</summary>
    private static readonly TimeSpan InvokeWait = TimeSpan.FromSeconds(3);

    private readonly Worker _worker = new();
    private readonly UIA3Automation _automation;
    private readonly int? _ownIntegrity = Integrity.Of(Environment.ProcessId);

    public UiaDesktop()
    {
        _automation = _worker.Run(() =>
        {
            var automation = new UIA3Automation
            {
                // Зависшая программа не должна держать все блоки окон бесконечно.
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                TransactionTimeout = TimeSpan.FromSeconds(20),
            };
            return automation;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public string? UnavailableReason => null;

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<WindowInfo>>(() => TopWindows(includeUntitled: false).Select(w => w.Info).ToList(), cancellationToken);

    public Task<IReadOnlyList<DesktopWindow>> FindWindowsAsync(WindowQuery query, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<DesktopWindow>>(() => TopWindows(includeUntitled: query.TitleContains is null)
            .Where(w => (query.TitleContains is null || w.Info.Title.Contains(query.TitleContains, StringComparison.CurrentCultureIgnoreCase)) &&
                        (query.ProcessName is null || string.Equals(w.Info.ProcessName, query.ProcessName, StringComparison.OrdinalIgnoreCase)))
            .Cast<DesktopWindow>()
            .ToList(), cancellationToken);

    public Task<IReadOnlyList<DesktopElement>> FindElementsAsync(DesktopWindow window, ElementQuery query, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<DesktopElement>>(() =>
        {
            var root = Native(window);
            var cf = _automation.ConditionFactory;
            var conditions = new List<ConditionBase>();
            if (query.AutomationId is { } id)
            {
                conditions.Add(cf.ByAutomationId(id));
            }

            if (query.ControlType is { } type)
            {
                conditions.Add(cf.ByControlType(Enum.TryParse<ControlType>(type, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new DesktopException($"Неизвестный тип элемента «{type}».")));
            }

            ConditionBase condition = conditions.Count switch
            {
                0 => TrueCondition.Default,
                1 => conditions[0],
                _ => new AndCondition(conditions.ToArray()),
            };
            return root.FindAllDescendants(condition)
                .Where(e => query.Name is null || NameMatches(SafeName(e), query))
                .Select(e => (DesktopElement)new UiaElement(e, Describe(e)))
                .ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<ElementNode>> DescribeAsync(DesktopWindow window, int maxDepth, int maxCount, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<ElementNode>>(() =>
        {
            // Тот же вид дерева, в котором ищут блоки (элементы управления, без служебных узлов).
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            var result = new List<ElementNode>();
            void Visit(AutomationElement parent, int depth)
            {
                if (depth > maxDepth)
                {
                    return;
                }

                var child = Safe(() => walker.GetFirstChild(parent));
                while (child is not null && result.Count < maxCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Элемент мог исчезнуть посреди обхода — его просто не будет в списке.
                    if (Safe(() => Describe(child)) is { } info)
                    {
                        result.Add(new ElementNode(depth, info));
                        Visit(child, depth + 1);
                    }

                    child = Safe(() => walker.GetNextSibling(child));
                }
            }

            Visit(Native(window), 1);
            return result;
        }, cancellationToken);

    private static T? Safe<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or FlaUI.Core.Exceptions.ElementNotAvailableException
                                       or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return null;
        }
    }

    public Task<bool> IsOpenAsync(DesktopWindow window, CancellationToken cancellationToken) =>
        RunAsync(() => window is UiaWindow { Info.Handle: var handle } && handle != 0 && NativeMethods.IsWindow(new IntPtr(handle)), cancellationToken);

    public Task<WindowInfo> ActivateAsync(DesktopWindow window, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var handle = Handle(window);
            try
            {
                if (!NativeMethods.IsWindow(handle))
                {
                    throw new DesktopInputBlockedException($"Окно «{window.Info.Title}» уже закрыто.");
                }

                if (NativeMethods.IsIconic(handle))
                {
                    NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
                }

                // Впереди должно быть само окно: его окно поменьше (например, «Найти») не в счёт — ввод пошёл бы туда. Если
                // программа показывает диалог, Windows выведет вперёд его — тогда ввод остановится с понятной причиной.
                if (NativeMethods.GetForegroundWindow() != handle)
                {
                    Native(window).SetForeground();
                    var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(700);
                    while (NativeMethods.GetForegroundWindow() != handle && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex) when (ex is not DesktopInputBlockedException)
            {
                throw new DesktopInputBlockedException($"Не удалось вывести окно «{window.Info.Title}» на передний план: {ex.Message}.");
            }

            RequireForeground(window);
            Thread.Sleep(100);
            return window.Info with { Bounds = Bounds(handle) };
        }, cancellationToken);

    public Task<InvokeOutcome> InvokeAsync(DesktopElement element, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var e = Native(element);
            Action? action = null;
            if (e.Patterns.Invoke.TryGetPattern(out var invoke))
            {
                action = invoke.Invoke;
            }
            else if (e.Patterns.Toggle.TryGetPattern(out var toggle))
            {
                action = toggle.Toggle;
            }
            else if (e.Patterns.SelectionItem.TryGetPattern(out var selection))
            {
                action = selection.Select;
            }

            if (action is null)
            {
                return InvokeOutcome.NotSupported;
            }

            // Некоторые программы (WinForms, Win32) не возвращаются из «нажать», пока открытый им диалог не закроют.
            // Нажатие — в отдельном потоке: общий поток Pepa для окон не должен ждать чужой диалог. Ошибка после того, как
            // ждать перестали, ничего уже не меняет.
            Exception? failure = null;
            var done = new ManualResetEventSlim();
            var call = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Pepa UI Automation Invoke",
            };
            call.SetApartmentState(ApartmentState.MTA);
            call.Start();
            if (!done.Wait(InvokeWait))
            {
                return InvokeOutcome.Pending;
            }

            done.Dispose();
            return failure is null ? InvokeOutcome.Done : throw new DesktopException(failure.Message, failure);
        }, cancellationToken);

    public Task ClickAsync(DesktopWindow window, DesktopElement element, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            RequireForeground(window);
            var e = Native(element);
            if (!VisiblePoint(e, out var point))
            {
                // За краем окна или прокручен: попросить прокрутить к нему и посмотреть ещё раз.
                if (e.Patterns.ScrollItem.TryGetPattern(out var scroll))
                {
                    scroll.ScrollIntoView();
                    Thread.Sleep(200);
                }

                if (!VisiblePoint(e, out point))
                {
                    throw new DesktopInputBlockedException(
                        $"Элемент {Describe(element)} сейчас не виден на экране (за краем окна, прокручен или свёрнут) — мышью в него не попасть.");
                }
            }

            if (!Hits(e, point))
            {
                throw new DesktopInputBlockedException(
                    $"В точке элемента {Describe(element)} сейчас другой элемент или окно (что-то его перекрывает) — мышью нажать нельзя.");
            }

            RequireForeground(window);
            if (doubleClick)
            {
                Mouse.DoubleClick(point, Button(button));
            }
            else
            {
                Mouse.Click(point, Button(button));
            }

            return true;
        }, cancellationToken);

    public Task<bool> SetValueAsync(DesktopElement element, string text, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var e = Native(element);
            if (!e.Patterns.Value.TryGetPattern(out var value) || value.IsReadOnly.ValueOrDefault)
            {
                return false;
            }

            value.SetValue(text);
            return true;
        }, cancellationToken);

    public Task FocusAsync(DesktopWindow window, DesktopElement element, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            RequireForeground(window);
            var e = Native(element);
            e.Focus();
            Thread.Sleep(50);
            if (!HasFocus(e))
            {
                throw new DesktopInputBlockedException($"Поле {Describe(element)} не получило фокус ввода — печатать в него нельзя.");
            }

            return true;
        }, cancellationToken);

    public Task<string?> ReadAsync(DesktopElement element, string what, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var e = Native(element);
            string? Value() => e.Patterns.Value.TryGetPattern(out var value) ? value.Value.ValueOrDefault : null;
            string? Text() => e.Patterns.Text.TryGetPattern(out var text) ? text.DocumentRange.GetText(-1) : null;
            return what switch
            {
                "name" => SafeName(e),
                "value" => Value() ?? Text(),
                _ => Value() is { Length: > 0 } v ? v : Text() is { Length: > 0 } t ? t : SafeName(e),
            };
        }, cancellationToken);

    public Task MouseClickAsync(DesktopWindow window, int x, int y, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            RequireForeground(window);
            var handle = Handle(window);
            var at = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y });
            var root = at == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(at, NativeMethods.GaRoot);
            // Только само окно: даже окно той же программы (диалог, панель) в этой точке — не то, куда просили нажать.
            if (root != handle)
            {
                throw new DesktopInputBlockedException($"В точке ({x}, {y}) сейчас другое окно — мышью нажать нельзя.");
            }

            if (doubleClick)
            {
                Mouse.DoubleClick(new DrawingPoint(x, y), Button(button));
            }
            else
            {
                Mouse.Click(new DrawingPoint(x, y), Button(button));
            }

            return true;
        }, cancellationToken);

    public Task TypeTextAsync(DesktopWindow window, string text, int delayMs, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            var sent = 0;
            RequireForeground(window);
            var focus = FocusOf(Handle(window));
            var refocus = false;
            foreach (var ch in text.ReplaceLineEndings("\n"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Перед каждым символом: окно всё ещё впереди (пользователь мог переключиться, программа — открыть диалог)
                // и ввод там же, где начинался (фокус не перешёл на другое поле).
                RequireForeground(window, partial: sent > 0);
                if (refocus)
                {
                    // Tab и Enter из текста переводят фокус сами — это задумано: дальше сверяем с новым полем.
                    Thread.Sleep(Math.Max(0, 80 - delayMs));
                    focus = FocusOf(Handle(window));
                    refocus = false;
                }
                else if (focus != IntPtr.Zero && FocusOf(Handle(window)) is var now && now != focus)
                {
                    if (now == IntPtr.Zero || !NativeMethods.IsChild(focus, now))
                    {
                        throw new DesktopInputBlockedException(
                            $"Во время ввода в окне «{window.Info.Title}» фокус перешёл на другое поле — Pepa остановилась, чтобы не печатать не туда.",
                            partial: sent > 0);
                    }

                    // Таблицы и списки на первом же символе открывают поле правки внутри себя — это тот же ввод.
                    focus = now;
                }

                switch (ch)
                {
                    case '\n':
                        SendInput.Keys([0x0D]);
                        refocus = true;
                        break;
                    case '\t':
                        SendInput.Keys([0x09]);
                        refocus = true;
                        break;
                    default:
                        SendInput.Unicode(ch);
                        break;
                }

                sent++;
                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
            }

            return true;
        }, cancellationToken);

    public Task PressKeysAsync(DesktopWindow window, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            var codes = keys.Select(VirtualKey).ToArray();
            RequireForeground(window);
            SendInput.Keys(codes);
            return true;
        }, cancellationToken);

    public Task<int> StartProgramAsync(string path, string? arguments, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path, arguments ?? "") { UseShellExecute = true });
                return process?.Id ?? 0;
            }
            catch (Win32Exception ex)
            {
                throw new DesktopException(ex.NativeErrorCode == 2 ? "файл не найден" : ex.Message, ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new DesktopException(ex.Message, ex);
            }
        }, cancellationToken);

    public Task CloseWindowAsync(DesktopWindow window, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            // Как нажатие на крестик. Сообщение ставится в очередь окна: Pepa не ждёт, пока программа ответит (например,
            // покажет вопрос «Сохранить?»).
            if (!NativeMethods.PostMessage(Handle(window), NativeMethods.WmSysCommand, new IntPtr(NativeMethods.ScClose), IntPtr.Zero))
            {
                throw new DesktopException($"Windows не передал окну «{window.Info.Title}» команду закрыться.");
            }

            return true;
        }, cancellationToken);

    public void Dispose()
    {
        try
        {
            _worker.Run(() =>
            {
                _automation.Dispose();
                return true;
            }, CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _worker.Dispose();
    }

    // ───────────── Передний план и права ─────────────

    /// <summary>Поле с фокусом ввода в потоке окна (0 — неизвестно: у программы нет отдельных окон-полей).</summary>
    private static IntPtr FocusOf(IntPtr handle)
    {
        var thread = NativeMethods.GetWindowThreadProcessId(handle, out _);
        var info = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        return thread != 0 && NativeMethods.GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    /// <summary>Окно (или его собственный диалог) на переднем плане — для вывода окна вперёд.</summary>
    private static bool OwnsForeground(IntPtr handle)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        return foreground == handle || NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot) == handle ||
               NativeMethods.GetAncestor(foreground, NativeMethods.GaRootOwner) == handle;
    }

    /// <summary>
    /// Ввод — только в окно на переднем плане и только если Windows его пропустит: в программу, запущенную от имени
    /// администратора, ввод из Pepa без таких прав молча теряется (Windows не сообщает об этом).
    /// </summary>
    private void RequireForeground(DesktopWindow window, bool partial = false)
    {
        var handle = Handle(window);
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != handle && NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot) != handle &&
            NativeMethods.GetAncestor(foreground, NativeMethods.GaRootOwner) == handle)
        {
            // Ввод — только в то окно, о котором шаг: диалог программы («Сохранить?») мог открыться сам, и буква или Enter
            // в нём выбрали бы ответ за пользователя.
            throw new DesktopInputBlockedException(
                $"Поверх окна «{window.Info.Title}» открыто другое окно этой программы (например, вопрос или диалог). Чтобы не ответить " +
                "в нём за вас, Pepa остановилась. Закройте его или добавьте в сценарий шаг для этого окна.", partial);
        }

        if (!OwnsForeground(handle))
        {
            throw new DesktopInputBlockedException(
                $"Окно «{window.Info.Title}» не на переднем плане: Windows не дал вывести его вперёд (так бывает, когда вы работаете в " +
                "другом окне) или на него переключились. Чтобы не нажать и не напечатать в чужом окне, Pepa остановилась.", partial);
        }

        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var processId);
        var target = Integrity.Of((int)processId);
        if (target is null || _ownIntegrity is null || target > _ownIntegrity)
        {
            throw new DesktopInputBlockedException(
                target is null
                    ? $"Не удалось проверить права программы окна «{window.Info.Title}». Если она запущена от имени администратора, ввод из " +
                      "Pepa до неё не дойдёт — запустите Pepa тоже от имени администратора или программу без этих прав."
                    : $"Программа окна «{window.Info.Title}» запущена от имени администратора: ввод мышью и клавиатурой из Pepa до неё не " +
                      "дойдёт (Windows его молча отбрасывает). Запустите Pepa тоже от имени администратора или программу без этих прав.",
                partial);
        }
    }

    /// <summary>Точка элемента на экране, если элемент сейчас виден.</summary>
    private static bool VisiblePoint(AutomationElement element, out DrawingPoint point)
    {
        point = default;
        if (element.Properties.IsOffscreen.ValueOrDefault)
        {
            return false;
        }

        var r = element.Properties.BoundingRectangle.ValueOrDefault;
        if (r.Width <= 0 || r.Height <= 0)
        {
            return false;
        }

        if (!element.TryGetClickablePoint(out point))
        {
            point = new DrawingPoint(r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        var screen = new System.Drawing.Rectangle(NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
        return r.Contains(point) && screen.Contains(point);
    }

    /// <summary>В точке — сам элемент или его часть (текст внутри кнопки и т.п.).</summary>
    private bool Hits(AutomationElement element, DrawingPoint point)
    {
        var walker = _automation.TreeWalkerFactory.GetRawViewWalker();
        var current = _automation.FromPoint(point);
        for (var depth = 0; current is not null && depth < 60; depth++)
        {
            if (_automation.Compare(current, element))
            {
                return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    /// <summary>Фокус ввода в элементе или внутри него.</summary>
    private bool HasFocus(AutomationElement element)
    {
        if (element.Properties.HasKeyboardFocus.ValueOrDefault)
        {
            return true;
        }

        var walker = _automation.TreeWalkerFactory.GetRawViewWalker();
        var current = _automation.FocusedElement();
        for (var depth = 0; current is not null && depth < 60; depth++)
        {
            if (_automation.Compare(current, element))
            {
                return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    private static DesktopRect Bounds(IntPtr handle) =>
        NativeMethods.GetWindowRect(handle, out var r)
            ? new DesktopRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : throw new DesktopInputBlockedException("Не удалось узнать положение окна.");

    // ───────────── Внутреннее ─────────────

    private List<UiaWindow> TopWindows(bool includeUntitled)
    {
        var result = new List<UiaWindow>();
        foreach (var element in _automation.GetDesktop().FindAllChildren())
        {
            // Окно может закрыться посреди перебора — тогда оно просто пропускается.
            try
            {
                var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
                var title = SafeName(element);
                if (handle == IntPtr.Zero || (title.Length == 0 && !includeUntitled))
                {
                    continue;
                }

                var processId = element.Properties.ProcessId.ValueOrDefault;
                if (processId == Environment.ProcessId)
                {
                    // Окна самой Pepa не трогаем.
                    continue;
                }

                var processName = WindowProcessName(element, processId);
                if (processName is null)
                {
                    continue;
                }

                var r = element.Properties.BoundingRectangle.ValueOrDefault;
                result.Add(new UiaWindow(element, new WindowInfo(handle.ToInt64(), title, processName, processId,
                    new DesktopRect(r.X, r.Y, r.Width, r.Height))));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or FlaUI.Core.Exceptions.ElementNotAvailableException
                                           or FlaUI.Core.Exceptions.PropertyNotSupportedException or TimeoutException)
            {
            }
        }

        return result;
    }

    /// <summary>Программа окна верхнего уровня (null — процесс уже завершился).</summary>
    internal static string? WindowProcessName(AutomationElement window, int processId)
    {
        var processName = ProcessName(processId);
        if (processName is not null && string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            // Приложения из Microsoft Store живут в рамке ApplicationFrameHost — настоящая программа внутри неё.
            processName = window.FindAllChildren()
                .Select(child => child.Properties.ProcessId.ValueOrDefault)
                .Where(id => id != processId && id != 0)
                .Select(ProcessName)
                .FirstOrDefault(name => name is not null) ?? processName;
        }

        return processName;
    }

    private static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Процесс уже завершился.
            return null;
        }
    }

    internal static ElementInfo Describe(AutomationElement element)
    {
        var r = element.Properties.BoundingRectangle.ValueOrDefault;
        return new ElementInfo(SafeName(element), element.Properties.ControlType.ValueOrDefault.ToString(),
            element.Properties.AutomationId.ValueOrDefault ?? "", new DesktopRect(r.X, r.Y, r.Width, r.Height),
            element.Properties.IsEnabled.ValueOrDefault);
    }

    private static string Describe(DesktopElement element) =>
        element.Info.Name is { Length: > 0 } name ? $"«{name}»" : $"{element.Info.ControlType} {element.Info.AutomationId}".Trim();

    internal static string SafeName(AutomationElement element) => element.Properties.Name.ValueOrDefault?.Trim() ?? "";

    private static bool NameMatches(string name, ElementQuery query) => query.NameContains
        ? name.Contains(query.Name!, StringComparison.CurrentCultureIgnoreCase)
        : string.Equals(name, query.Name, StringComparison.CurrentCultureIgnoreCase);

    private static IntPtr Handle(DesktopWindow window) => new(window.Info.Handle);

    private static AutomationElement Native(DesktopWindow window) =>
        window is UiaWindow uia ? uia.Element : throw new DesktopException("Окно получено не из этого модуля.");

    private static AutomationElement Native(DesktopElement element) =>
        element is UiaElement uia ? uia.Element : throw new DesktopException("Элемент получен не из этого модуля.");

    private static MouseButton Button(MouseButtonKind button) => button switch
    {
        MouseButtonKind.Right => MouseButton.Right,
        MouseButtonKind.Middle => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private static ushort VirtualKey(string key) => key switch
    {
        "Ctrl" => 0x11,
        "Shift" => 0x10,
        "Alt" => 0x12,
        "Win" => 0x5B,
        "Enter" => 0x0D,
        "Tab" => 0x09,
        "Escape" => 0x1B,
        "Space" => 0x20,
        "Backspace" => 0x08,
        "Delete" => 0x2E,
        "Insert" => 0x2D,
        "Home" => 0x24,
        "End" => 0x23,
        "PageUp" => 0x21,
        "PageDown" => 0x22,
        "Left" => 0x25,
        "Up" => 0x26,
        "Right" => 0x27,
        "Down" => 0x28,
        "PrintScreen" => 0x2C,
        "Apps" => 0x5D,
        { Length: 1 } single when char.IsAsciiLetterOrDigit(single[0]) => single[0],
        ['F', .. var number] when int.TryParse(number, out var f) && f is >= 1 and <= 24 => (ushort)(0x70 + f - 1),
        _ => throw new DesktopException($"Неизвестная клавиша «{key}»."),
    };

    /// <summary>Вызов в потоке UI Automation; ошибки Windows и UI Automation — <see cref="DesktopException"/>.</summary>
    private Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken) =>
        _worker.Run(() =>
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is not (DesktopException or DesktopInputBlockedException or OperationCanceledException))
            {
                throw new DesktopException(ex.Message, ex);
            }
        }, cancellationToken);

    private Task RunVoidAsync(Func<bool> action, CancellationToken cancellationToken) => RunAsync(action, cancellationToken);

    /// <summary>
    /// Собственный поток для UI Automation (многопоточный режим COM). Вызовы выполняются строго по одному: UI Automation,
    /// мышь и клавиатура — общие для всего компьютера.
    /// </summary>
    private sealed class Worker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public Worker()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "Pepa UI Automation",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    result.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                }
            }, CancellationToken.None);
            // Отменённый шаг перестаёт ждать; начатый вызов UI Automation доработает сам (его ограничивают тайм-ауты UIA).
            return result.Task.WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }

    private sealed class UiaWindow(AutomationElement element, WindowInfo info) : DesktopWindow
    {
        public AutomationElement Element { get; } = element;
        public override WindowInfo Info { get; } = info;
    }

    private sealed class UiaElement(AutomationElement element, ElementInfo info) : DesktopElement
    {
        public AutomationElement Element { get; } = element;
        public override ElementInfo Info { get; } = info;
    }
}

/// <summary>Уровень прав процесса (обычный, администратор…): ввод в процесс с более высоким уровнем Windows не пропускает.</summary>
internal static class Integrity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    /// <returns>Уровень (RID метки целостности) или null, если узнать не удалось.</returns>
    public static int? Of(int processId)
    {
        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                NativeMethods.GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
                if (size == 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!NativeMethods.GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                    {
                        return null;
                    }

                    // TOKEN_MANDATORY_LABEL: первым полем — указатель на SID метки; уровень — последний под-идентификатор SID.
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}

/// <summary>Клавиатура через SendInput: клавиши — виртуальными кодами, текст — символами Unicode.</summary>
internal static class SendInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint UnicodeFlag = 0x0004;
    private const uint ExtendedKey = 0x0001;

    /// <summary>Нажать по порядку и отпустить в обратном (для сочетаний вроде Ctrl+Shift+S).</summary>
    public static void Keys(ushort[] keys)
    {
        var inputs = new List<NativeMethods.Input>();
        inputs.AddRange(keys.Select(k => Key(k, 0)));
        inputs.AddRange(keys.Reverse().Select(k => Key(k, KeyUp)));
        Send(inputs.ToArray());
    }

    public static void Unicode(char ch) =>
        Send([Char(ch, UnicodeFlag), Char(ch, UnicodeFlag | KeyUp)]);

    private static NativeMethods.Input Key(ushort vk, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new NativeMethods.KeyboardInput
        {
            VirtualKey = vk,
            Flags = flags | (IsExtended(vk) ? ExtendedKey : 0),
        },
    };

    private static NativeMethods.Input Char(char ch, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new NativeMethods.KeyboardInput { ScanCode = ch, Flags = flags },
    };

    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5D;

    private static void Send(NativeMethods.Input[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Length)
        {
            throw new DesktopException("Windows не принял нажатия клавиш (ввод заблокирован другой программой).");
        }
    }
}

internal static partial class NativeMethods
{
    public const uint WmSysCommand = 0x0112;
    public const int ScClose = 0xF060;
    public const int SwRestore = 9;
    public const uint GaRoot = 2;
    public const uint GaRootOwner = 3;
    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Union;

        public KeyboardInput Keyboard
        {
            readonly get => Union.Keyboard;
            set => Union.Keyboard = value;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public Rect CaretRect;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsChild(IntPtr parent, IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr handle, int command);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr handle, uint flags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr handle, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, uint length, out uint returned);

    [LibraryImport("advapi32.dll")]
    public static partial IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [LibraryImport("advapi32.dll")]
    public static partial IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
