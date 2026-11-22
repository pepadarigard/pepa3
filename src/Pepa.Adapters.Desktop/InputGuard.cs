namespace Pepa.Adapters.Desktop;

/// <summary>Что Windows сейчас знает о переднем плане — для решения, можно ли вводить в окно (см. <see cref="InputGuard"/>).</summary>
/// <param name="Window">Окно шага.</param>
/// <param name="Foreground">Окно на переднем плане (0 — нет).</param>
/// <param name="ForegroundRoot">Корневое окно переднего (GA_ROOT).</param>
/// <param name="ForegroundRootOwner">Владелец переднего окна (GA_ROOTOWNER): у диалога программы — её главное окно.</param>
/// <param name="TargetIntegrity">Уровень прав программы переднего окна; null — узнать не удалось.</param>
/// <param name="OwnIntegrity">Уровень прав самой Pepa; null — узнать не удалось.</param>
public readonly record struct ForegroundState(
    nint Window, nint Foreground, nint ForegroundRoot, nint ForegroundRootOwner, int? TargetIntegrity, int? OwnIntegrity);

/// <summary>Окно, в которое печатает «Ввести текст»: проверки и ввод по одному символу (реализация — UI Automation в Windows).</summary>
public interface ITypingTarget
{
    /// <summary>Окно впереди и ввод в него разрешён; иначе — <see cref="DesktopInputBlockedException"/> с <paramref name="partial"/>.</summary>
    void RequireForeground(bool partial);

    /// <summary>Окно шага.</summary>
    nint Window { get; }

    /// <summary>Элемент с фокусом ввода в потоке окна (0 — нет).</summary>
    nint Focus();

    bool IsChild(nint parent, nint child);

    /// <summary>Отправить символ: «\n» — Enter, «\t» — Tab, остальные — как текст.</summary>
    void Send(char ch);

    void Sleep(int milliseconds);
}

/// <summary>
/// Защита ввода в окна Windows, без самих вызовов Windows — чтобы её можно было проверить тестами. Ввод идёт только в окно
/// шага, пока оно впереди и фокус там же, где начинался: иначе остаток текста (возможно, пароль) ушёл бы в чужую программу.
/// Остановка после части текста — <c>partial</c>: итог неизвестен, повторять вслепую нельзя.
/// </summary>
public static class InputGuard
{
    /// <summary>Почему вводить в окно сейчас нельзя; null — можно.</summary>
    public static string? ForegroundProblem(ForegroundState s, string title)
    {
        if (s.Foreground != 0 && s.Foreground != s.Window && s.ForegroundRoot != s.Window && s.ForegroundRootOwner == s.Window)
        {
            // Ввод — только в то окно, о котором шаг: диалог программы («Сохранить?») мог открыться сам, и буква или Enter
            // в нём выбрали бы ответ за пользователя.
            return $"Поверх окна «{title}» открыто другое окно этой программы (например, вопрос или диалог). Чтобы не ответить " +
                   "в нём за вас, Pepa остановилась. Закройте его или добавьте в сценарий шаг для этого окна.";
        }

        var owns = s.Foreground != 0 &&
                   (s.Foreground == s.Window || s.ForegroundRoot == s.Window || s.ForegroundRootOwner == s.Window);
        if (!owns)
        {
            return $"Окно «{title}» не на переднем плане: Windows не дал вывести его вперёд (так бывает, когда вы работаете в " +
                   "другом окне) или на него переключились. Чтобы не нажать и не напечатать в чужом окне, Pepa остановилась.";
        }

        if (s.TargetIntegrity is null || s.OwnIntegrity is null || s.TargetIntegrity > s.OwnIntegrity)
        {
            return s.TargetIntegrity is null
                ? $"Не удалось проверить права программы окна «{title}». Если она запущена от имени администратора, ввод из " +
                  "Pepa до неё не дойдёт — запустите Pepa тоже от имени администратора или программу без этих прав."
                : $"Программа окна «{title}» запущена от имени администратора: ввод мышью и клавиатурой из Pepa до неё не " +
                  "дойдёт (Windows его молча отбрасывает). Запустите Pepa тоже от имени администратора или программу без этих прав.";
        }

        return null;
    }

    /// <summary>Напечатать текст по символу, перед каждым проверяя окно и фокус.</summary>
    public static void TypeText(ITypingTarget target, string text, int delayMs, string title, CancellationToken cancellationToken)
    {
        var sent = 0;
        target.RequireForeground(partial: false);
        var focus = target.Focus();
        var refocus = false;
        var followed = false;
        var sentHere = 0;
        foreach (var ch in text.ReplaceLineEndings("\n"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Перед каждым символом: окно всё ещё впереди (пользователь мог переключиться, программа — открыть диалог)
            // и ввод там же, где начинался (фокус не перешёл на другое поле).
            target.RequireForeground(partial: sent > 0);
            if (refocus)
            {
                // Tab и Enter из текста переводят фокус сами — это задумано: дальше сверяем с новым полем.
                target.Sleep(Math.Max(0, 80 - delayMs));
                focus = target.Focus();
                refocus = false;
                followed = false;
                sentHere = 0;
            }
            else if (focus != 0 && target.Focus() is var now && now != focus)
            {
                // Таблицы и списки на первом же символе открывают поле правки внутри себя — это тот же ввод. Только так:
                // сразу после первого символа, один раз, внутрь того же поля (не окна целиком). Иначе фокус ушёл не туда.
                if (now == 0 || followed || sentHere != 1 || focus == target.Window || !target.IsChild(focus, now))
                {
                    throw new DesktopInputBlockedException(
                        $"Во время ввода в окне «{title}» фокус перешёл на другое поле — Pepa остановилась, чтобы не печатать не туда.",
                        partial: sent > 0);
                }

                focus = now;
                followed = true;
            }

            target.Send(ch);
            if (ch is '\n' or '\t')
            {
                refocus = true;
            }

            sent++;
            sentHere++;
            if (delayMs > 0)
            {
                target.Sleep(delayMs);
            }
        }
    }
}
