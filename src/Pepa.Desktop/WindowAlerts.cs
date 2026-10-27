using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Pepa.Host.Notifications;

namespace Pepa.Desktop;

/// <summary>
/// Процесс ждёт решения, а окно Pepa не перед пользователем (свёрнуто или под другими окнами) — кнопка Pepa на панели
/// задач мигает, пока окно не откроют. Окно на переднем плане не мигает: там и так видно.
/// </summary>
internal sealed class WindowAlerts : IDesktopAlerts
{
    private const uint FlashAll = 3;
    private const uint FlashUntilForeground = 12;

    private Window? _window;

    public void Attach(Window window) => _window = window;

    public void RequestAttention(string text)
    {
        if (_window is not { } window)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsActive)
            {
                return;
            }

            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = new WindowInteropHelper(window).Handle,
                Flags = FlashAll | FlashUntilForeground,
                Count = uint.MaxValue,
                Timeout = 0,
            };
            FlashWindowEx(ref info);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
