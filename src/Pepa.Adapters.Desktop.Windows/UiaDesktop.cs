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
/// Окна Windows через UI Automation (FlaUI, UIA3). Вызовы выполняются по одному: UI Automation и ввод — общие для
/// всего компьютера. Текст печатается символами Unicode — от раскладки клавиатуры он не зависит.
/// </summary>
public sealed class UiaDesktop : IDesktop, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? UnavailableReason => null;

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<WindowInfo>>(() => TopWindows().Select(w => w.Info).ToList(), cancellationToken);

    public Task<IReadOnlyList<DesktopWindow>> FindWindowsAsync(WindowQuery query, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<DesktopWindow>>(() => TopWindows()
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

    public Task<bool> IsOpenAsync(DesktopWindow window, CancellationToken cancellationToken) =>
        RunAsync(() => window is UiaWindow { Info.Handle: var handle } && handle != 0 && NativeMethods.IsWindow(new IntPtr(handle)), cancellationToken);

    public Task ActivateAsync(DesktopWindow window, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            var element = Native(window);
            if (element.Patterns.Window.TryGetPattern(out var pattern) && pattern.WindowVisualState.ValueOrDefault == WindowVisualState.Minimized)
            {
                pattern.SetWindowVisualState(WindowVisualState.Normal);
            }

            element.SetForeground();
            Thread.Sleep(150);
            return true;
        }, cancellationToken);

    public Task<bool> InvokeAsync(DesktopElement element, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var e = Native(element);
            if (e.Patterns.Invoke.TryGetPattern(out var invoke))
            {
                invoke.Invoke();
                return true;
            }

            if (e.Patterns.Toggle.TryGetPattern(out var toggle))
            {
                toggle.Toggle();
                return true;
            }

            if (e.Patterns.SelectionItem.TryGetPattern(out var selection))
            {
                selection.Select();
                return true;
            }

            return false;
        }, cancellationToken);

    public Task ClickAsync(DesktopElement element, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            var e = Native(element);
            var point = e.TryGetClickablePoint(out var clickable)
                ? clickable
                : new DrawingPoint(element.Info.Bounds.CenterX, element.Info.Bounds.CenterY);
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

    public Task FocusAsync(DesktopElement element, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            Native(element).Focus();
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

    public Task MouseClickAsync(int x, int y, MouseButtonKind button, bool doubleClick, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
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

    public Task TypeTextAsync(string text, int delayMs, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            foreach (var ch in text.ReplaceLineEndings("\n"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (ch)
                {
                    case '\n':
                        SendInput.Keys([0x0D]);
                        break;
                    case '\t':
                        SendInput.Keys([0x09]);
                        break;
                    default:
                        SendInput.Unicode(ch);
                        break;
                }

                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
            }

            return true;
        }, cancellationToken);

    public Task PressKeysAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        RunVoidAsync(() =>
        {
            SendInput.Keys(keys.Select(VirtualKey).ToArray());
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
            var element = Native(window);
            if (element.Patterns.Window.TryGetPattern(out var pattern))
            {
                pattern.Close();
            }
            else
            {
                NativeMethods.PostMessage(new IntPtr(window.Info.Handle), NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, cancellationToken);

    public void Dispose()
    {
        _automation.Dispose();
        _gate.Dispose();
    }

    // ───────────── Внутреннее ─────────────

    private List<UiaWindow> TopWindows()
    {
        var result = new List<UiaWindow>();
        foreach (var element in _automation.GetDesktop().FindAllChildren())
        {
            var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
            var title = SafeName(element);
            if (handle == IntPtr.Zero || title.Length == 0)
            {
                continue;
            }

            var processId = element.Properties.ProcessId.ValueOrDefault;
            if (processId == Environment.ProcessId)
            {
                // Окна самой Pepa не трогаем.
                continue;
            }

            string processName;
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                continue;
            }

            var r = element.Properties.BoundingRectangle.ValueOrDefault;
            result.Add(new UiaWindow(element, new WindowInfo(handle.ToInt64(), title, processName, processId,
                new DesktopRect(r.X, r.Y, r.Width, r.Height))));
        }

        return result;
    }

    private static ElementInfo Describe(AutomationElement element)
    {
        var r = element.Properties.BoundingRectangle.ValueOrDefault;
        return new ElementInfo(SafeName(element), element.Properties.ControlType.ValueOrDefault.ToString(),
            element.Properties.AutomationId.ValueOrDefault ?? "", new DesktopRect(r.X, r.Y, r.Width, r.Height),
            element.Properties.IsEnabled.ValueOrDefault);
    }

    private static string SafeName(AutomationElement element) => element.Properties.Name.ValueOrDefault?.Trim() ?? "";

    private static bool NameMatches(string name, ElementQuery query) => query.NameContains
        ? name.Contains(query.Name!, StringComparison.CurrentCultureIgnoreCase)
        : string.Equals(name, query.Name, StringComparison.CurrentCultureIgnoreCase);

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

    /// <summary>По одному вызову за раз, в фоне; ошибки Windows и UI Automation — <see cref="DesktopException"/>.</summary>
    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return action();
                }
                catch (Exception ex) when (ex is not (DesktopException or OperationCanceledException))
                {
                    throw new DesktopException(ex.Message, ex);
                }
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunVoidAsync(Func<bool> action, CancellationToken cancellationToken) => RunAsync(action, cancellationToken);

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
            throw new DesktopException(
                "Windows не принял нажатия клавиш (так бывает, если активное окно запущено от администратора, а Pepa — нет).");
        }
    }
}

internal static partial class NativeMethods
{
    public const uint WmClose = 0x0010;

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

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr handle);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
