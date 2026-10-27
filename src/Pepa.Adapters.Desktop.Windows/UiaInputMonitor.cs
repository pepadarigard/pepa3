using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using DrawingPoint = System.Drawing.Point;

namespace Pepa.Adapters.Desktop.Windows;

/// <summary>
/// Перехват мыши и клавиатуры для записи действий в окнах Windows: глобальные перехватчики (low-level hooks) только
/// отмечают, что и где нажато, и сразу возвращают управление; элементы под мышью и в фокусе определяет UI Automation в
/// отдельном потоке, а подсчёт похожих элементов (долгий в больших окнах) — в третьем, чтобы следующие нажатия
/// определялись сразу, пока окно не изменилось. Ввод программ (SendInput, в том числе блоков Pepa) и ввод в окна самой
/// Pepa не записывается.
/// </summary>
public sealed class UiaInputMonitor : IDesktopInputMonitor
{
    public string? UnavailableReason => null;

    /// <summary>Как в настройках мыши Windows.</summary>
    public TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(HookNative.GetDoubleClickTime());

    public int DoubleClickDistance => Math.Max(2, HookNative.GetSystemMetrics(HookNative.SmCxDoubleClk) / 2);

    /// <summary>Сколько может задержаться определение элемента, прежде чем у шага появится пояснение.</summary>
    private static readonly TimeSpan LagNote = TimeSpan.FromMilliseconds(300);

    public IDesktopInputWatch Start(Action<DesktopInput> sink)
    {
        var watch = new Watch(sink);
        watch.Start();
        return watch;
    }

    private enum RawKind
    {
        MouseDown,
        MouseUp,
        Wheel,
        KeyDown,
        KeyUp,
    }

    /// <summary>Сырое событие из перехватчика: только то, что можно узнать мгновенно.</summary>
    /// <param name="Modifiers">Какие из Ctrl, Alt, Shift, Win зажаты в момент события (<see cref="Mods"/>).</param>
    private readonly record struct Raw(RawKind Kind, DateTimeOffset At, int X, int Y, MouseButtonKind Button, int Vk, bool Own, int RootProcessId,
        int Modifiers = 0);

    private static class Mods
    {
        public const int Ctrl = 1, Alt = 2, Shift = 4, Win = 8;

        /// <summary>
        /// Состояние клавиш-модификаторов прямо сейчас (в обратном вызове перехвата). Не по нажатиям: после Ctrl+Alt+Del или
        /// Win+L Windows не сообщает перехватчику, что клавиши отпущены, и «зажатый» Ctrl испортил бы всю запись.
        /// </summary>
        public static int Now() =>
            (HookNative.GetAsyncKeyState(0x11) < 0 ? Ctrl : 0) | (HookNative.GetAsyncKeyState(0x12) < 0 ? Alt : 0) |
            (HookNative.GetAsyncKeyState(0x10) < 0 ? Shift : 0) |
            (HookNative.GetAsyncKeyState(0x5B) < 0 || HookNative.GetAsyncKeyState(0x5C) < 0 ? Win : 0);
    }

    /// <summary>Действие, ожидающее подсчёта похожих элементов (окно — для поиска).</summary>
    private sealed record Pending(DesktopInput Input, AutomationElement? Window);

    private sealed class Watch(Action<DesktopInput> sink) : IDesktopInputWatch
    {
        /// <summary>Перехватчик один на процесс: функция обратного вызова статическая.</summary>
        private static Watch? s_active;

        private readonly BlockingCollection<Raw> _raw = new();
        private readonly BlockingCollection<Pending> _pending = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private UIA3Automation? _automation;
        private Thread? _hookThread;
        private uint _hookThreadId;
        private nint _mouseHook;
        private nint _keyboardHook;

        // Состояние потока разбора (только из него).
        private bool _altAlone;
        private Typing? _typing;
        private readonly Dictionary<MouseButtonKind, (Raw Down, DesktopTarget? Target, AutomationElement? Window, bool Modifiers)> _down = [];

        /// <summary>Набор текста в поле: само поле, что о нём записать и последнее прочтённое во время набора значение.</summary>
        private sealed class Typing(AutomationElement element, DesktopTarget target)
        {
            public AutomationElement Element { get; } = element;
            public DesktopTarget Target { get; } = target;
            public string? LastSeen { get; set; }

            /// <summary>Последняя клавиша набора стирала (Backspace, Delete, Ctrl+X): пустое поле — это ввод пользователя.</summary>
            public bool LastKeyErased { get; set; }

            /// <summary>
            /// Поле большое или отвечает медленно (документ в Word): во время набора его больше не читаем, чтобы не задерживать
            /// разбор нажатий мышью.
            /// </summary>
            public bool SkipReads { get; set; }

            /// <summary>Сколько клавиш набора нажато после последнего прочтения: больше одной — прочтённое устарело.</summary>
            public int KeysSinceRead { get; set; }
        }

        /// <summary>Чтение поля во время набора дольше этого — дальше поле во время набора не читается.</summary>
        private static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(40);

        private const int MaxReadLength = 20_000;

        public void Start()
        {
            lock (typeof(Watch))
            {
                if (s_active is not null)
                {
                    throw new DesktopException("Запись в окнах Windows уже идёт.");
                }

                s_active = this;
            }

            try
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _hookThread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "Pepa запись: перехват" };
                _hookThread.Start();
                ready.Task.GetAwaiter().GetResult();

                _automation = new UIA3Automation { ConnectionTimeout = TimeSpan.FromSeconds(5), TransactionTimeout = TimeSpan.FromSeconds(10) };
                var parse = new Thread(ParseLoop) { IsBackground = true, Name = "Pepa запись: элементы" };
                parse.SetApartmentState(ApartmentState.MTA);
                parse.Start();
                var count = new Thread(CountLoop) { IsBackground = true, Name = "Pepa запись: подсчёт" };
                count.SetApartmentState(ApartmentState.MTA);
                count.Start();
            }
            catch
            {
                // Перехватчики уже стоят — снять их, а не оставить работать без записи.
                if (_hookThreadId != 0)
                {
                    HookNative.PostThreadMessage(_hookThreadId, HookNative.WmQuit, 0, 0);
                }

                Release();
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (_hookThreadId != 0)
            {
                HookNative.PostThreadMessage(_hookThreadId, HookNative.WmQuit, 0, 0);
            }

            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        private void Release()
        {
            lock (typeof(Watch))
            {
                if (s_active == this)
                {
                    s_active = null;
                }
            }
        }

        // ───────────── Поток перехвата ─────────────

        private unsafe void HookLoop(TaskCompletionSource ready)
        {
            try
            {
                var module = HookNative.GetModuleHandle(null);
                _mouseHook = HookNative.SetWindowsHookEx(HookNative.WhMouseLl, &MouseProc, module, 0);
                _keyboardHook = HookNative.SetWindowsHookEx(HookNative.WhKeyboardLl, &KeyboardProc, module, 0);
                if (_mouseHook == 0 || _keyboardHook == 0)
                {
                    throw new DesktopException($"Windows не дал перехватывать мышь и клавиатуру (код {Marshal.GetLastPInvokeError()}).");
                }

                _hookThreadId = HookNative.GetCurrentThreadId();
                ready.SetResult();
            }
            catch (Exception ex)
            {
                Unhook();
                _raw.CompleteAdding();
                ready.TrySetException(ex);
                return;
            }

            // Перехватчики работают, пока этот поток разбирает сообщения.
            while (HookNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                HookNative.TranslateMessage(message);
                HookNative.DispatchMessage(message);
            }

            Unhook();
            Release();
            _raw.CompleteAdding();
        }

        private void Unhook()
        {
            if (_mouseHook != 0)
            {
                HookNative.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = 0;
            }

            if (_keyboardHook != 0)
            {
                HookNative.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = 0;
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe nint MouseProc(int code, nint message, nint data)
        {
            if (code >= 0 && s_active is { } watch)
            {
                try
                {
                    var info = (HookNative.MouseInfo*)data;
                    if ((info->Flags & (HookNative.LlmhfInjected | HookNative.LlmhfLowerIlInjected)) == 0)
                    {
                        var kind = (int)message switch
                        {
                            0x0201 or 0x0204 or 0x0207 => RawKind.MouseDown,
                            0x0202 or 0x0205 or 0x0208 => RawKind.MouseUp,
                            0x020A or 0x020E => RawKind.Wheel,
                            _ => (RawKind?)null,
                        };
                        if (kind is { } k)
                        {
                            var button = (int)message switch
                            {
                                0x0204 or 0x0205 => MouseButtonKind.Right,
                                0x0207 or 0x0208 => MouseButtonKind.Middle,
                                _ => MouseButtonKind.Left,
                            };
                            var root = HookNative.GetAncestor(HookNative.WindowFromPoint(info->Point), HookNative.GaRoot);
                            HookNative.GetWindowThreadProcessId(root, out var processId);
                            watch._raw.TryAdd(new Raw(k, DateTimeOffset.Now, info->Point.X, info->Point.Y, button, 0,
                                processId == Environment.ProcessId, (int)processId, k == RawKind.MouseDown ? Mods.Now() : 0));
                        }
                    }
                }
                catch
                {
                    // Исключение из обратного вызова уронило бы Pepa; пропущенное событие хуже не сделает.
                }
            }

            return HookNative.CallNextHookEx(0, code, message, data);
        }

        [UnmanagedCallersOnly]
        private static unsafe nint KeyboardProc(int code, nint message, nint data)
        {
            if (code >= 0 && s_active is { } watch)
            {
                try
                {
                    var info = (HookNative.KeyboardInfo*)data;
                    if ((info->Flags & HookNative.LlkhfInjected) == 0)
                    {
                        var down = (int)message is 0x0100 or 0x0104;
                        HookNative.GetWindowThreadProcessId(HookNative.GetForegroundWindow(), out var processId);
                        // Сама нажатая клавиша в этом состоянии ещё не учтена — только зажатые до неё.
                        watch._raw.TryAdd(new Raw(down ? RawKind.KeyDown : RawKind.KeyUp, DateTimeOffset.Now, 0, 0, MouseButtonKind.Left,
                            (int)info->VkCode, processId == Environment.ProcessId, (int)processId, down ? Mods.Now() : 0));
                    }
                }
                catch
                {
                }
            }

            return HookNative.CallNextHookEx(0, code, message, data);
        }

        // ───────────── Поток разбора ─────────────

        private void ParseLoop()
        {
            try
            {
                foreach (var raw in _raw.GetConsumingEnumerable())
                {
                    try
                    {
                        Parse(raw);
                    }
                    catch (Exception ex) when (!IsFatal(ex))
                    {
                        // Любая ошибка UI Automation или программы — пояснение к записи, а не падение Pepa.
                        Emit(new DesktopSkippedInput(raw.At, $"Windows не ответил, что за элемент под мышью или в фокусе ({ex.Message}) — действие не записано"));
                    }
                }

                FlushTyping(DateTimeOffset.Now);
            }
            finally
            {
                _pending.CompleteAdding();
            }
        }

        private void Parse(Raw raw)
        {
            switch (raw.Kind)
            {
                case RawKind.Wheel:
                    if (!raw.Own)
                    {
                        Emit(new DesktopSkippedInput(raw.At, "прокрутка колесом мыши не записывается (блоки находят элементы и без неё)"));
                    }

                    break;
                case RawKind.MouseDown:
                    _altAlone = false;
                    // Набор в поле закончился: прочитать текст, пока нажатие ничего не изменило.
                    FlushTyping(raw.At);
                    if (raw.Own)
                    {
                        _down.Remove(raw.Button);
                        break;
                    }

                    var (target, window) = Resolve(raw);
                    _down[raw.Button] = (raw, target, window, (raw.Modifiers & (Mods.Ctrl | Mods.Alt | Mods.Shift)) != 0);
                    break;
                case RawKind.MouseUp:
                    if (!_down.Remove(raw.Button, out var pressed) || pressed.Target is null)
                    {
                        break;
                    }

                    if (IsDrag(pressed.Down, raw, pressed.Target))
                    {
                        Emit(new DesktopSkippedInput(raw.At, "перетаскивание мышью не записывается"));
                        break;
                    }

                    Emit(new DesktopClickInput(pressed.Down.At, pressed.Target, pressed.Down.X, pressed.Down.Y, raw.Button, pressed.Modifiers),
                        pressed.Window);
                    break;
                case RawKind.KeyDown:
                    OnKeyDown(raw);
                    break;
                case RawKind.KeyUp:
                    if (_typing is { } typed && !DesktopKeyRules.IsModifier(raw.Vk))
                    {
                        // Прочитать поле, когда программа уже приняла клавишу: если после Enter она сразу очистит поле,
                        // останется текст со всеми символами.
                        Remember(typed);
                    }

                    if (raw.Vk is 0x12 or 0xA4 or 0xA5 && _altAlone)
                    {
                        _altAlone = false;
                        if (!raw.Own)
                        {
                            Key(raw, 0x12);
                        }
                    }

                    break;
            }
        }

        private void OnKeyDown(Raw raw)
        {
            if (DesktopKeyRules.IsModifier(raw.Vk))
            {
                // Alt уже был зажат (это повтор), если состояние клавиатуры до этого нажатия говорит так. Не по нажатиям:
                // после Ctrl+Alt+Del отпускание Alt перехватчик не видит.
                var wasDown = (raw.Modifiers & Mods.Alt) != 0;
                if (raw.Vk is 0x12 or 0xA4 or 0xA5)
                {
                    // Повтор зажатого Alt не сбрасывает «нажат один Alt»; Alt вместе с Ctrl — это AltGr или сочетание.
                    _altAlone = (wasDown ? _altAlone : true) && (raw.Modifiers & (Mods.Ctrl | Mods.Shift | Mods.Win)) == 0;
                }
                else
                {
                    _altAlone = false;
                }

                return;
            }

            _altAlone = false;
            if (raw.Own)
            {
                return;
            }

            Key(raw, raw.Vk);
        }

        /// <summary>Элементы-«пункты»: сдвинутое в их пределах нажатие — всё ещё нажатие на них.</summary>
        private static readonly HashSet<string> ItemTypes = new(
            ["Button", "SplitButton", "MenuItem", "Hyperlink", "CheckBox", "RadioButton", "TabItem", "ListItem", "TreeItem", "HeaderItem",
             "ComboBox", "DataItem"],
            StringComparer.Ordinal);

        /// <summary>
        /// Перетаскивание — мышь ушла дальше, чем Windows считает щелчком. Чуть сдвинутое нажатие на кнопку или пункт (отпущено
        /// над тем же элементом) остаётся нажатием; в списке, тексте, холсте любое такое движение — выделение рамкой или текста,
        /// и щелчок сделал бы другое.
        /// </summary>
        private static bool IsDrag(Raw down, Raw up, DesktopTarget target)
        {
            var moved = Math.Abs(up.X - down.X) > Math.Max(2, HookNative.GetSystemMetrics(HookNative.SmCxDrag)) ||
                        Math.Abs(up.Y - down.Y) > Math.Max(2, HookNative.GetSystemMetrics(HookNative.SmCyDrag));
            if (!moved)
            {
                return false;
            }

            return target.Element is not { Bounds: var b } element || !ItemTypes.Contains(element.ControlType) ||
                   up.X < b.X || up.Y < b.Y || up.X >= b.X + b.Width || up.Y >= b.Y + b.Height;
        }

        private void Key(Raw raw, int vk)
        {
            var focused = _automation!.FocusedElement();
            var mods = raw.Modifiers;
            var ctrl = (mods & Mods.Ctrl) != 0;
            var decision = DesktopKeyRules.Classify(vk, ctrl, (mods & Mods.Alt) != 0 || vk is 0x12, (mods & Mods.Shift) != 0, (mods & Mods.Win) != 0,
                Editable(focused));
            switch (decision.Role)
            {
                case KeyRole.Typing:
                    if (_typing is { } typing && !Same(typing.Element, focused))
                    {
                        FlushTyping(raw.At);
                    }

                    if (_typing is null && Target(focused, raw) is { } target)
                    {
                        _typing = new Typing(focused, target.Target);
                    }

                    if (_typing is { } active)
                    {
                        active.LastKeyErased = vk is 0x08 or 0x2E || (ctrl && vk == 0x58);
                        active.KeysSinceRead++;
                    }

                    break;
                case KeyRole.Key:
                    FlushTyping(raw.At);
                    if (Target(focused, raw) is { } keyTarget)
                    {
                        Emit(new DesktopKeyInput(raw.At, keyTarget.Target, decision.Keys!), keyTarget.Window);
                    }

                    break;
                default:
                    if (decision.Note is { } note)
                    {
                        Emit(new DesktopSkippedInput(raw.At, note));
                    }

                    break;
            }
        }


        /// <summary>Запомнить текущее значение поля во время набора (не дольше <see cref="SlowRead"/>).</summary>
        private static void Remember(Typing typing)
        {
            if (typing.SkipReads || typing.Target.IsPassword)
            {
                return;
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var text = TryRead(typing);
            if (text is { Length: > MaxReadLength })
            {
                typing.SkipReads = true;
                typing.LastSeen = null;
                return;
            }

            // Медленное чтение (первое обращение к Electron, Chromium) годится, но дальше поле во время набора не читаем.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > SlowRead)
            {
                typing.SkipReads = true;
            }

            // Пустое поле не заменяет прочитанный текст, если его не стирали: значит, программа его уже очистила (отправила).
            if (!string.IsNullOrEmpty(text) || typing.LastKeyErased)
            {
                typing.LastSeen = text ?? typing.LastSeen;
                if (text is not null)
                {
                    typing.KeysSinceRead = 0;
                }
            }
        }

        private void FlushTyping(DateTimeOffset at)
        {
            if (_typing is not { } typing)
            {
                return;
            }

            _typing = null;
            var text = TryRead(typing);
            var early = false;
            if (!typing.Target.IsPassword && string.IsNullOrEmpty(text) && !typing.LastKeyErased)
            {
                // Поле пустое (или закрылось), хотя последним вводили символ, — программа очистила его сама (Enter отправил
                // сообщение). Берём прочтённое во время набора — с пометкой, что последний символ мог не попасть; не прочли
                // ничего — текст неизвестен (шаг попросит вписать его), а не пустой.
                // Если после прочтения нажато больше одной клавиши (поле перестали читать — оно медленное), прочтённое — лишь
                // начало текста: текст неизвестен.
                text = typing.LastSeen is { Length: > 0 } seen && typing.KeysSinceRead <= 1 ? seen : null;
                early = text is not null;
            }

            Emit(new DesktopTextInput(at, typing.Target, text, early), WindowOf(typing.Element));
        }

        /// <summary>Значение поля; null — пароль (не читается) или поле уже не прочитать.</summary>
        private static string? TryRead(Typing typing)
        {
            if (typing.Target.IsPassword)
            {
                return null;
            }

            try
            {
                return ReadText(typing.Element);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                // Поле закрылось вместе с окном — текст не прочитать; шаг будет с пояснением.
                return null;
            }
        }

        private static string? ReadText(AutomationElement element)
        {
            if (element.Patterns.Value.TryGetPattern(out var value))
            {
                return value.Value.ValueOrDefault;
            }

            return element.Patterns.Text.TryGetPattern(out var text) ? text.DocumentRange.GetText(-1) : null;
        }

        private static bool Editable(AutomationElement? element)
        {
            if (element is null)
            {
                return false;
            }

            var type = element.Properties.ControlType.ValueOrDefault;
            return element.Patterns.Value.TryGetPattern(out var value)
                ? !value.IsReadOnly.ValueOrDefault && type is ControlType.Edit or ControlType.Document or ControlType.ComboBox
                : type is ControlType.Edit or ControlType.Document && element.Patterns.Text.IsSupported;
        }

        /// <summary>Что под мышью: элемент и его окно верхнего уровня (null — окно, которое не записывается).</summary>
        private (DesktopTarget? Target, AutomationElement? Window) Resolve(Raw raw)
        {
            var element = _automation!.FromPoint(new DrawingPoint(raw.X, raw.Y));
            var result = Target(element, raw);
            if (result is null)
            {
                return (null, null);
            }

            var target = result.Value.Target;
            if (target.Window.ProcessId != raw.RootProcessId && raw.RootProcessId != 0)
            {
                target = target with
                {
                    Note = string.Join("; ", new[] { target.Note, "под мышью во время нажатия сменилось окно — проверьте, тот ли элемент записан" }
                        .Where(n => n is not null)),
                };
            }

            return (target, result.Value.Window);
        }

        /// <summary>
        /// Окно и сведения об элементе; null — окна Pepa и оболочки Windows (не записываются) или окно не определилось
        /// (тогда — пояснение к записи: действие не записано, но и не пропало молча).
        /// </summary>
        private (DesktopTarget Target, AutomationElement Window)? Target(AutomationElement? element, Raw raw)
        {
            const string unknown = "Pepa не смогла определить окно (оно закрылось или не отвечает) — действие не записано";
            if (element is null)
            {
                Emit(new DesktopSkippedInput(raw.At, unknown));
                return null;
            }

            var window = WindowOf(element);
            if (window is null)
            {
                Emit(new DesktopSkippedInput(raw.At, unknown));
                return null;
            }

            var processId = window.Properties.ProcessId.ValueOrDefault;
            if (processId == Environment.ProcessId)
            {
                return null;
            }

            var processName = UiaDesktop.WindowProcessName(window, processId);
            if (processName is null)
            {
                Emit(new DesktopSkippedInput(raw.At, unknown));
                return null;
            }

            if (IsShell(window, processName))
            {
                Emit(new DesktopSkippedInput(raw.At,
                    "действия на панели задач, рабочем столе и в меню «Пуск» не записываются — программу запускает блок «Запустить программу»"));
                return null;
            }

            var r = window.Properties.BoundingRectangle.ValueOrDefault;
            var info = new WindowInfo(window.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(), UiaDesktop.SafeName(window), processName, processId,
                new DesktopRect(r.X, r.Y, r.Width, r.Height));
            // Нажатие в само окно (фон, рамка) — элемента нет, будет нажатие в точке.
            var described = Same(element, window) ? null : UiaDesktop.Describe(element);
            // Окно ответило не сразу — к этому времени фокус или элемент под мышью могли уже смениться.
            var late = DateTimeOffset.Now - raw.At > LagNote
                ? "Windows долго отвечал, что за элемент, — проверьте, тот ли элемент записан"
                : null;
            return (new DesktopTarget { Window = info, Element = described, IsPassword = element.Properties.IsPassword.ValueOrDefault, Note = late },
                window);
        }

        /// <summary>Окно верхнего уровня элемента — так же, как окна перебирают блоки (дети «рабочего стола» UI Automation).</summary>
        private AutomationElement? WindowOf(AutomationElement element)
        {
            try
            {
                var walker = _automation!.TreeWalkerFactory.GetControlViewWalker();
                var root = _automation.GetDesktop();
                var current = element;
                while (true)
                {
                    var parent = walker.GetParent(current);
                    if (parent is null)
                    {
                        return null;
                    }

                    if (Same(parent, root))
                    {
                        return current;
                    }

                    current = parent;
                }
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                return null;
            }
        }

        private static bool IsShell(AutomationElement window, string processName)
        {
            if (processName is "StartMenuExperienceHost" or "SearchHost" or "SearchApp" or "ShellExperienceHost")
            {
                return true;
            }

            return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) &&
                   window.Properties.ClassName.ValueOrDefault is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW";
        }

        private bool Same(AutomationElement a, AutomationElement b)
        {
            try
            {
                return _automation!.Compare(a, b);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                return false;
            }
        }

        private void Emit(DesktopInput input, AutomationElement? window = null)
        {
            if (!_pending.IsAddingCompleted)
            {
                _pending.Add(new Pending(input, window));
            }
        }

        // ───────────── Поток подсчёта ─────────────

        private void CountLoop()
        {
            try
            {
                foreach (var pending in _pending.GetConsumingEnumerable())
                {
                    var input = pending.Input;
                    try
                    {
                        input = input switch
                        {
                            DesktopClickInput click => click with { Target = Count(click.Target, pending.Window) },
                            DesktopTextInput text => text with { Target = Count(text.Target, pending.Window) },
                            // Блок «Клавиатура» элемент не ищет — считать незачем.
                            _ => input,
                        };
                    }
                    catch (Exception ex) when (!IsFatal(ex))
                    {
                        // Окно закрылось — похожие элементы не подсчитать: шаг всё равно записывается, с пояснением.
                        input = input switch
                        {
                            DesktopClickInput click => click with { Target = click.Target with { Note = Unchecked(click.Target) } },
                            DesktopTextInput text => text with { Target = text.Target with { Note = Unchecked(text.Target) } },
                            _ => input,
                        };
                    }

                    try
                    {
                        sink(input);
                    }
                    catch
                    {
                        // Запись не должна прерываться из-за получателя.
                    }
                }
            }
            finally
            {
                _automation?.Dispose();
                Release();
                _stopped.TrySetResult();
            }
        }

        private static string Unchecked(DesktopTarget target) =>
            string.Join("; ", new[] { target.Note, "окно закрылось раньше, чем Pepa проверила, единственный ли это элемент" }.Where(n => n is not null));

        /// <summary>Сколько в окне элементов с тем же кодом и с той же подписью — так же, как их ищет блок.</summary>
        private DesktopTarget Count(DesktopTarget target, AutomationElement? window)
        {
            if (window is null || target.Element is not { } element)
            {
                return target;
            }

            var cf = _automation!.ConditionFactory;
            var sameId = element.AutomationId.Length > 0 ? window.FindAllDescendants(cf.ByAutomationId(element.AutomationId)).Length : 0;
            var sameName = 0;
            if (element.Name.Length > 0)
            {
                ConditionBase condition = DesktopNodes.ControlTypes.Any(t => t.Value == element.ControlType) &&
                                          Enum.TryParse<ControlType>(element.ControlType, out var type)
                    ? cf.ByControlType(type)
                    : TrueCondition.Default;
                sameName = window.FindAllDescendants(condition)
                    .Count(e => string.Equals(UiaDesktop.SafeName(e), element.Name, StringComparison.CurrentCultureIgnoreCase));
            }

            return target with { SameId = sameId, SameName = sameName };
        }

        /// <summary>
        /// Ошибки, после которых процесс не жилец; все остальные (COM переводит свои коды в самые разные исключения .NET) —
        /// пояснение к записи. Необработанное исключение в этом потоке уронило бы всю Pepa вместе с идущими процессами.
        /// </summary>
        private static bool IsFatal(Exception ex) => ex is OutOfMemoryException or AccessViolationException or StackOverflowException;
    }
}

internal static partial class HookNative
{
    public const int WhMouseLl = 14;
    public const int WhKeyboardLl = 13;
    public const uint WmQuit = 0x0012;
    public const uint GaRoot = 2;
    public const uint LlmhfInjected = 0x01;
    public const uint LlmhfLowerIlInjected = 0x02;
    public const uint LlkhfInjected = 0x10;
    public const int SmCxDoubleClk = 36;
    public const int SmCxDrag = 68;
    public const int SmCyDrag = 69;

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInfo
    {
        public NativeMethods.Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInfo
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativeMethods.Point Point;
        public uint Private;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static unsafe partial nint SetWindowsHookEx(int hook, delegate* unmanaged<int, nint, nint, nint> callback, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Message message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(NativeMethods.Point point);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);
}
