namespace Pepa.Adapters.Desktop.Tests;

/// <summary>
/// Защита ввода в окна Windows (то, что не даёт напечатать остаток текста — возможно, пароля — в чужую программу). Сами
/// вызовы Windows не проверяются здесь, а решения — проверяются: окно впереди, фокус там же, где начинался ввод.
/// </summary>
public sealed class InputGuardTests
{
    private const nint Window = 100;
    private const nint Field = 101;
    private const nint Cell = 102;
    private const nint Other = 200;

    /// <summary>Окно: фокус и передний план меняются по сценарию теста после n-го отправленного символа.</summary>
    private sealed class FakeTarget : ITypingTarget
    {
        public nint Window => InputGuardTests.Window;
        public nint CurrentFocus { get; set; } = Field;
        public bool Foreground { get; set; } = true;
        public HashSet<(nint Parent, nint Child)> Children { get; } = [(InputGuardTests.Window, Field), (InputGuardTests.Window, Cell), (Field, Cell)];
        public List<char> Sent { get; } = [];
        public Action<FakeTarget>? AfterSend { get; set; }

        public void RequireForeground(bool partial)
        {
            if (!Foreground)
            {
                throw new DesktopInputBlockedException("окно не впереди", partial);
            }
        }

        public nint Focus() => CurrentFocus;

        public bool IsChild(nint parent, nint child) => Children.Contains((parent, child));

        public void Send(char ch)
        {
            Sent.Add(ch);
            AfterSend?.Invoke(this);
        }

        public void Sleep(int milliseconds)
        {
        }
    }

    private static void Type(FakeTarget target, string text) =>
        InputGuard.TypeText(target, text, 0, "Блокнот", CancellationToken.None);

    [Fact]
    public void Text_is_typed_when_the_window_stays_in_front_and_focus_stays_put()
    {
        var target = new FakeTarget();
        Type(target, "привет");
        Assert.Equal("привет", new string([.. target.Sent]));
    }

    [Fact]
    public void Window_not_in_front_blocks_before_the_first_key_and_nothing_is_sent()
    {
        var target = new FakeTarget { Foreground = false };
        var blocked = Assert.Throws<DesktopInputBlockedException>(() => Type(target, "секрет"));
        Assert.False(blocked.Partial);
        Assert.Empty(target.Sent);
    }

    [Fact]
    public void Switching_windows_mid_text_stops_the_rest_and_marks_the_outcome_unknown()
    {
        var target = new FakeTarget { AfterSend = t => t.Foreground = t.Sent.Count < 3 };
        var blocked = Assert.Throws<DesktopInputBlockedException>(() => Type(target, "Qwerty123"));
        Assert.True(blocked.Partial);
        Assert.Equal("Qwe", new string([.. target.Sent]));
    }

    [Fact]
    public void Focus_may_follow_into_an_edit_field_the_control_opens_after_the_first_character()
    {
        // Таблица на первом символе открывает поле правки внутри себя — это тот же ввод.
        var target = new FakeTarget { AfterSend = t => { if (t.Sent.Count == 1) t.CurrentFocus = Cell; } };
        Type(target, "12345");
        Assert.Equal("12345", new string([.. target.Sent]));
    }

    [Fact]
    public void Focus_moving_to_another_field_stops_with_partial_input()
    {
        var target = new FakeTarget { AfterSend = t => { if (t.Sent.Count == 3) t.CurrentFocus = Other; } };
        var blocked = Assert.Throws<DesktopInputBlockedException>(() => Type(target, "пароль"));
        Assert.True(blocked.Partial);
        Assert.Contains("фокус перешёл", blocked.Message);
        Assert.Equal(3, target.Sent.Count);
    }

    [Fact]
    public void Second_focus_change_after_following_into_the_cell_stops()
    {
        var target = new FakeTarget
        {
            AfterSend = t =>
            {
                if (t.Sent.Count == 1) t.CurrentFocus = Cell;
                if (t.Sent.Count == 2) t.CurrentFocus = Field;
            },
        };
        var blocked = Assert.Throws<DesktopInputBlockedException>(() => Type(target, "abcd"));
        Assert.True(blocked.Partial);
        Assert.Equal(2, target.Sent.Count);
    }

    [Fact]
    public void Focus_jump_to_an_unrelated_element_after_the_first_character_stops()
    {
        // Сразу после первого символа фокус ушёл не внутрь поля, а в чужой элемент — это не «поле правки таблицы».
        var target = new FakeTarget { AfterSend = t => { if (t.Sent.Count == 1) t.CurrentFocus = Other; } };
        Assert.True(Assert.Throws<DesktopInputBlockedException>(() => Type(target, "abc")).Partial);
        Assert.Single(target.Sent);
    }

    [Fact]
    public void Tab_and_Enter_in_the_text_move_focus_on_purpose()
    {
        var target = new FakeTarget { AfterSend = t => { if (t.Sent[^1] == '\t') t.CurrentFocus = Other; } };
        Type(target, "логин\tпароль");
        Assert.Equal("логин\tпароль", new string([.. target.Sent]));
    }

    [Fact]
    public void Cancellation_stops_between_characters()
    {
        using var cancel = new CancellationTokenSource();
        var target = new FakeTarget { AfterSend = t => { if (t.Sent.Count == 2) cancel.Cancel(); } };
        Assert.Throws<OperationCanceledException>(() => InputGuard.TypeText(target, "abcdef", 0, "Блокнот", cancel.Token));
        Assert.Equal(2, target.Sent.Count);
    }

    [Fact]
    public void Own_dialog_on_top_blocks_input_into_the_main_window()
    {
        // «Сохранить изменения?» открылся сам: Enter в нём выбрал бы ответ за пользователя.
        var problem = InputGuard.ForegroundProblem(new ForegroundState(Window, Other, Other, Window, 2, 2), "Блокнот");
        Assert.Contains("другое окно этой программы", problem);
    }

    [Fact]
    public void Foreground_rules_allow_the_window_and_its_child_and_refuse_others_and_admin_programs()
    {
        Assert.Null(InputGuard.ForegroundProblem(new ForegroundState(Window, Window, Window, Window, 2, 2), "Блокнот"));
        Assert.Null(InputGuard.ForegroundProblem(new ForegroundState(Window, Field, Window, Window, 2, 2), "Блокнот"));
        Assert.Contains("не на переднем плане", InputGuard.ForegroundProblem(new ForegroundState(Window, Other, Other, Other, 2, 2), "Блокнот"));
        Assert.Contains("не на переднем плане", InputGuard.ForegroundProblem(new ForegroundState(Window, 0, 0, 0, 2, 2), "Блокнот"));
        Assert.Contains("от имени администратора", InputGuard.ForegroundProblem(new ForegroundState(Window, Window, Window, Window, 3, 2), "Блокнот"));
        Assert.Contains("Не удалось проверить права", InputGuard.ForegroundProblem(new ForegroundState(Window, Window, Window, Window, null, 2), "Блокнот"));
    }
}
