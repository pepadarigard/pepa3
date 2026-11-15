using System.Runtime.InteropServices;
using System.Text;

namespace Pepa.Desktop;

/// <summary>
/// Завершение сеанса Windows (D-086). Pepa останавливается, только когда сеанс действительно заканчивается (WM_ENDSESSION),
/// а не на вопросе «можно ли завершить» (WM_QUERYENDSESSION): завершение после него ещё могут отменить (Word спросил о
/// сохранении, пользователь нажал «Отмена»), и Pepa осталась бы закрытой — процессы на паузе, расписания стоят.
/// <para>
/// Сам WPF на этот вопрос закрывает приложение (Application.Shutdown), а отменить это можно только отказом от завершения
/// сеанса. Поэтому вопрос перехватывается в скрытом окне приложения WPF раньше WPF: ответ «можно» — и больше ничего.
/// </para>
/// </summary>
internal sealed class SessionEnd
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const long ENDSESSION_CLOSEAPP = 0x1;
    private const int GWLP_WNDPROC = -4;

    private readonly Action<bool> _ended;
    private readonly WndProc _proc;
    private readonly List<(IntPtr Window, IntPtr Previous)> _hooked = [];
    private bool _done;

    private SessionEnd(Action<bool> ended)
    {
        _ended = ended;
        // Делегат живёт вместе с объектом: Windows вызывает его по адресу.
        _proc = Hook;
    }

    /// <summary>Окно, в котором перехвачен вопрос (для ShutdownBlockReasonCreate).</summary>
    public IntPtr Window => _hooked.Count > 0 ? _hooked[0].Window : IntPtr.Zero;

    /// <summary>
    /// Перехватить в скрытых окнах WPF этого потока (вызывать до показа окна Pepa). <paramref name="ended"/> вызывается один
    /// раз, когда сеанс заканчивается; аргумент — Windows закрывает только Pepa (обновление программы, Restart Manager).
    /// </summary>
    /// <returns>null — окна не нашлись: остаётся поведение WPF по умолчанию.</returns>
    public static SessionEnd? TryAttach(Action<bool> ended)
    {
        var watcher = new SessionEnd(ended);
        var windows = new List<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var name = new StringBuilder(256);
            if (GetClassName(window, name, name.Capacity) > 0 && name.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        var pointer = Marshal.GetFunctionPointerForDelegate(watcher._proc);
        foreach (var window in windows)
        {
            var previous = SetWindowProc(window, pointer);
            if (previous != IntPtr.Zero)
            {
                watcher._hooked.Add((window, previous));
            }
        }

        return watcher._hooked.Count > 0 ? watcher : null;
    }

    private IntPtr Hook(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_QUERYENDSESSION:
                // Можно завершать — но сами ещё ничего не останавливаем: завершение могут отменить.
                return 1;
            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero && !_done)
                {
                    _done = true;
                    _ended((lParam.ToInt64() & ENDSESSION_CLOSEAPP) != 0);
                }

                // wParam = 0: завершение отменили — Pepa работает дальше, как работала.
                return IntPtr.Zero;
        }

        var previous = _hooked.FirstOrDefault(h => h.Window == window).Previous;
        return previous == IntPtr.Zero ? DefWindowProc(window, message, wParam, lParam) : CallWindowProc(previous, window, message, wParam, lParam);
    }

    /// <summary>Что показывает Windows, пока Pepa останавливается (вместо «Эта программа препятствует завершению работы»).</summary>
    public void SetBlockReason(string text)
    {
        if (Window != IntPtr.Zero)
        {
            _ = ShutdownBlockReasonCreate(Window, text);
        }
    }

    /// <summary>
    /// После обновления программы (Restart Manager закрыл Pepa) — запустить снова, свёрнутой. После перезагрузки — нет: тогда
    /// запускает автозапуск, если он включён.
    /// </summary>
    public static void RegisterRestart(string arguments)
    {
        const int RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;
        try
        {
            _ = RegisterApplicationRestart(arguments, RestartNoCrash | RestartNoHang | RestartNoReboot);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Не на Windows — перезапускать нечего.
        }
    }

    private static IntPtr SetWindowProc(IntPtr window, IntPtr proc) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, GWLP_WNDPROC, proc) : new IntPtr(SetWindowLong32(window, GWLP_WNDPROC, proc.ToInt32()));

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string arguments, int flags);
}

/// <summary>Заблокирован ли сеанс сейчас (Win+L) — для запуска Pepa уже на заблокированном компьютере.</summary>
internal static class SessionLock
{
    private const int WTSSessionInfoEx = 25;
    private const int WTS_SESSIONSTATE_LOCK = 0;
    private const int WTS_CURRENT_SESSION = -1;

    /// <summary>true — заблокирован; false — нет или узнать не удалось (тогда не держим ввод зря: разблокировки бы не было).</summary>
    public static bool IsLocked()
    {
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx, out var buffer, out var bytes) || buffer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // WTSINFOEXW: Level (DWORD), затем объединение, выровненное на 8 (в нём LARGE_INTEGER): SessionId, SessionState,
                // SessionFlags — по смещению 16.
                return bytes >= 20 && Marshal.ReadInt32(buffer) == 1 && Marshal.ReadInt32(buffer, 16) == WTS_SESSIONSTATE_LOCK;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
