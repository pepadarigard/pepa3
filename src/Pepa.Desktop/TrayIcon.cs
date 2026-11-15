using System.Windows;
using Forms = System.Windows.Forms;

namespace Pepa.Desktop;

/// <summary>
/// Значок Pepa в области уведомлений (D-086): открыть окно, что сейчас делается (в подсказке), свернуть при закрытии,
/// запуск вместе с Windows, выход. Пока окно свёрнуто сюда, движок работает, а о решениях сообщает всплывающее сообщение.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _closeToTray;
    private readonly Forms.ToolStripMenuItem _autostart;

    /// <summary>
    /// Отметки меняет код (<see cref="SetChecks"/>): обработчики молчат. Иначе смена отметки снова писала бы в реестр, а при
    /// ошибке записи возврат отметки снова звал бы запись — и предупреждения шли бы без конца.
    /// </summary>
    private bool _suppress;
    private bool _exiting;

    public TrayIcon(Action open, Action exit, Action<bool> closeToTray, Action<bool> startWithWindows)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Pepa", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _closeToTray = new Forms.ToolStripMenuItem("При закрытии окна — сворачивать сюда") { CheckOnClick = true };
        _closeToTray.CheckedChanged += (_, _) =>
        {
            if (!_suppress)
            {
                closeToTray(_closeToTray.Checked);
            }
        };
        menu.Items.Add(_closeToTray);
        _autostart = new Forms.ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true };
        _autostart.CheckedChanged += (_, _) =>
        {
            if (!_suppress)
            {
                startWithWindows(_autostart.Checked);
            }
        };
        menu.Items.Add(_autostart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выйти из Pepa", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Pepa",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
    }

    /// <summary>Отметки в меню — без вызова обработчиков (при запуске и когда изменение не удалось).</summary>
    public void SetChecks(bool closeToTray, bool startWithWindows)
    {
        _suppress = true;
        try
        {
            _closeToTray.Checked = closeToTray;
            _autostart.Checked = startWithWindows;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>Только отметка «сворачивать при закрытии»: запуск вместе с Windows остаётся, как есть в меню (и в реестре).</summary>
    public void SetCloseToTray(bool closeToTray) => SetChecks(closeToTray, _autostart.Checked);

    /// <summary>Pepa закрывается: подсказка говорит об этом, а обновления подсказки больше не меняют её.</summary>
    public void SetExiting()
    {
        _exiting = true;
        _icon.Text = "Pepa закрывается…";
    }

    /// <summary>Подсказка над значком: Windows показывает не больше 127 символов.</summary>
    public void SetText(string text)
    {
        if (!_exiting)
        {
            _icon.Text = text.Length > 127 ? text[..126] + "…" : text;
        }
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(10_000, title, text.Length > 250 ? text[..249] + "…" : text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pepa.ico"));
        return resource is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(resource.Stream);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
