using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Desktop.Tests;

/// <summary>Запись действий в окнах: случаи, где шаг легко потерять, удвоить или записать не тем способом.</summary>
public sealed class DesktopRecordingEdgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static DesktopTarget Target(FakeWindow window, FakeElement? element, int sameId = 0, int sameName = 1, bool password = false) => new()
    {
        Window = window.Info,
        Element = element?.Info,
        SameId = sameId,
        SameName = sameName,
        IsPassword = password,
    };

    private static async Task<(DesktopRecorder Recorder, FakeInputMonitor Monitor)> StartAsync()
    {
        var monitor = new FakeInputMonitor();
        var recorder = new DesktopRecorder(monitor);
        await recorder.StartAsync(CancellationToken.None);
        return (recorder, monitor);
    }

    private static string Text(RecordedDesktopStep step, string field = "text") => step.Config[field]!.GetValue<string>();

    [Fact]
    public async Task Enter_that_sent_a_message_stays_even_when_the_next_message_has_two_lines()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Чат");
        var box = window.Add("Сообщение", type: "Edit", hasValue: true);
        var (recorder, monitor) = await StartAsync();

        // «привет» и Enter — сообщение ушло, поле очистилось. Потом второе сообщение из двух строк (Shift+Enter).
        monitor.Send(new DesktopTextInput(At(0), Target(window, box), "привет"));
        monitor.Send(new DesktopKeyInput(At(1), Target(window, box), "Enter"));
        monitor.Send(new DesktopTextInput(At(2), Target(window, box), "a"));
        monitor.Send(new DesktopKeyInput(At(3), Target(window, box), "Shift+Enter"));
        monitor.Send(new DesktopTextInput(At(4), Target(window, box), "a\nb"));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["desktop.fill", "desktop.keys", "desktop.fill"], steps.Select(s => s.Type));
        Assert.Equal("привет", Text(steps[0]));
        Assert.Equal("Enter", Text(steps[1], "keys"));
        Assert.Equal("a\nb", Text(steps[2]));
    }

    [Fact]
    public async Task Text_pasted_after_enter_keeps_the_enter()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Чат");
        var box = window.Add("Сообщение", type: "Edit", hasValue: true);
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopTextInput(At(0), Target(window, box), "привет"));
        monitor.Send(new DesktopKeyInput(At(1), Target(window, box), "Enter"));
        // Вставлен двухстрочный текст в очищенное поле.
        monitor.Send(new DesktopTextInput(At(2), Target(window, box), "x\ny"));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["desktop.fill", "desktop.keys", "desktop.fill"], steps.Select(s => s.Type));
        Assert.Equal("x\ny", Text(steps[2]));
    }

    [Fact]
    public async Task Newline_already_read_into_the_field_is_not_typed_twice()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Заметки");
        var note = window.Add("Текст", type: "Edit", hasValue: true);
        var other = window.Add("Комментарий", type: "Edit", hasValue: true);
        var code = window.Add("Код", type: "Edit", hasValue: true);
        var (recorder, monitor) = await StartAsync();

        // Поле прочитано уже после Enter: перевод строки в тексте, и текст продолжился — один шаг с итоговым текстом.
        monitor.Send(new DesktopTextInput(At(0), Target(window, note), "строка 1\r\n"));
        monitor.Send(new DesktopKeyInput(At(1), Target(window, note), "Enter"));
        monitor.Send(new DesktopTextInput(At(2), Target(window, note), "строка 1\r\nстрока 2"));
        // Набор закончился Enter: ввод без перевода строки, а Enter — шагом (иначе строк стало бы две).
        monitor.Send(new DesktopTextInput(At(3), Target(window, other), "готово\n"));
        monitor.Send(new DesktopKeyInput(At(4), Target(window, other), "Enter"));
        // То же с Tab.
        monitor.Send(new DesktopTextInput(At(5), Target(window, code), "if\t"));
        monitor.Send(new DesktopKeyInput(At(6), Target(window, code), "Tab"));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["desktop.fill", "desktop.fill", "desktop.keys", "desktop.fill", "desktop.keys"], steps.Select(s => s.Type));
        Assert.Equal("строка 1\r\nстрока 2", Text(steps[0]));
        Assert.Equal("готово", Text(steps[1]));
        Assert.Equal("if", Text(steps[3]));
        Assert.Equal("Tab", Text(steps[4], "keys"));
    }

    [Fact]
    public async Task List_item_is_clicked_with_the_mouse_because_invoking_it_would_open_it()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Проводник");
        var file = window.Add("Отчёт.docx", type: "ListItem");
        var ok = window.Add("OK");
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopClickInput(At(0), Target(window, file), 300, 400, MouseButtonKind.Left));
        monitor.Send(new DesktopClickInput(At(2), Target(window, ok), 500, 400, MouseButtonKind.Left));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal("mouse", Text(steps[0], "how"));
        Assert.Equal("auto", Text(steps[1], "how"));

        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterDesktopNodes(d));
        var scenario = new ScenarioBuilder("Выделить").Node("start", "start")
            .Node("rec1", steps[0].Type, steps[0].Config).Node("rec2", steps[1].Type, steps[1].Config)
            .Node("end", "end", new { message = "готово" }).Chain("start", "rec1", "rec2", "end").Build();
        var id = await h.RunAsync(scenario);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var actions = d.Actions.Where(a => !a.StartsWith("activate", StringComparison.Ordinal) && !a.StartsWith("focus", StringComparison.Ordinal));
        // Файл выделен щелчком, а не открыт; кнопка нажата без мыши.
        Assert.Equal(["click Отчёт.docx", "invoke OK"], actions);
    }

    [Fact]
    public async Task Click_with_ctrl_is_not_recorded_as_a_plain_click_and_the_next_step_explains_it()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Проводник");
        var first = window.Add("Первый", type: "ListItem");
        var second = window.Add("Второй", type: "ListItem");
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopClickInput(At(0), Target(window, first), 300, 400, MouseButtonKind.Left));
        monitor.Send(new DesktopClickInput(At(1), Target(window, second), 300, 440, MouseButtonKind.Left, WithModifiers: true));
        monitor.Send(new DesktopKeyInput(At(2), Target(window, second), "Delete"));
        // Последнее действие тоже с Ctrl — пояснение у последнего шага.
        monitor.Send(new DesktopClickInput(At(3), Target(window, first), 300, 400, MouseButtonKind.Left, WithModifiers: true));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["desktop.click", "desktop.keys"], steps.Select(s => s.Type));
        Assert.Contains("с Ctrl, Shift или Alt не записано", steps[1].Note);
        Assert.StartsWith("перед этим шагом:", steps[1].Note);
        Assert.Contains("после этого шага: нажатие мышью с Ctrl", steps[1].Note);
    }

    [Fact]
    public async Task Letters_typed_outside_a_field_are_not_recorded_but_single_letters_and_shortcuts_are()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Удалённый рабочий стол");
        var list = window.Add("Список", type: "List");
        var (recorder, monitor) = await StartAsync();

        // Одна буква в списке — переход к пункту: шаг остаётся.
        monitor.Send(new DesktopKeyInput(At(0), Target(window, list), "D"));
        monitor.Send(new DesktopClickInput(At(1), Target(window, list), 300, 400, MouseButtonKind.Left));
        // Набор вне поля (окно удалённого рабочего стола не сообщает о полях) — это мог быть пароль.
        foreach (var (key, i) in new[] { "S", "Shift+E", "C", "R", "E", "T" }.Select((k, i) => (k, i)))
        {
            monitor.Send(new DesktopKeyInput(At(2 + i * 0.1), Target(window, null), key));
        }

        monitor.Send(new DesktopKeyInput(At(3), Target(window, null), "Enter"));
        monitor.Send(new DesktopKeyInput(At(4), Target(window, null), "Alt+F"));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["D", "Enter", "Alt+F"], steps.Where(s => s.Type == "desktop.keys").Select(s => Text(s, "keys")));
        var enter = steps.Single(s => s.Type == "desktop.keys" && Text(s, "keys") == "Enter");
        Assert.Contains("набор текста вне поля ввода не записывается", enter.Note);
        Assert.Null(steps[^1].Note);
    }

    [Fact]
    public async Task Middle_click_is_not_merged_with_the_next_left_click_but_two_clicks_where_no_element_is_found_are()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Браузер");
        var tab = window.Add("Вкладка", type: "TabItem");
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopClickInput(At(0), Target(window, tab), 300, 400, MouseButtonKind.Middle));
        monitor.Send(new DesktopClickInput(At(0.3), Target(window, tab), 300, 400, MouseButtonKind.Left));
        // Элемент не определился — двойной щелчок в точке окна.
        monitor.Send(new DesktopClickInput(At(5), Target(window, null), 150, 260, MouseButtonKind.Left));
        monitor.Send(new DesktopClickInput(At(5.2), Target(window, null), 151, 261, MouseButtonKind.Left));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["desktop.mouse", "desktop.click", "desktop.mouse"], steps.Select(s => s.Type));
        Assert.Equal("middle", Text(steps[0], "button"));
        Assert.False(steps[0].Config["double"]!.GetValue<bool>());
        Assert.Equal("auto", Text(steps[1], "how"));
        Assert.True(steps[2].Config["double"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Each_password_field_gets_its_own_variable()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Смена пароля");
        var old = window.Add("Старый пароль", type: "Edit", hasValue: true);
        var fresh = window.Add("Новый пароль", type: "Edit", hasValue: true);
        var ok = window.Add("OK");
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopTextInput(At(0), Target(window, old, password: true), null));
        monitor.Send(new DesktopTextInput(At(1), Target(window, fresh, password: true), null));
        monitor.Send(new DesktopClickInput(At(2), Target(window, ok), 500, 400, MouseButtonKind.Left));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["password", "password2"], steps.Where(s => s.Secret).Select(s => s.SecretVariable));
        Assert.Equal("{{ vars.password }}", Text(steps[0]));
        Assert.Equal("{{ vars.password2 }}", Text(steps[1]));

        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterDesktopNodes(d));
        var scenario = new ScenarioBuilder("Пароль")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("password2", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start").Node("rec1", steps[0].Type, steps[0].Config).Node("rec2", steps[1].Type, steps[1].Config)
            .Node("end", "end", new { message = "готово" }).Chain("start", "rec1", "rec2", "end").Build();
        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "старый-123", ["password2"] = "новый-456" });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("старый-123", old.Value);
        Assert.Equal("новый-456", fresh.Value);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message));
        Assert.DoesNotContain("старый-123", journal);
        Assert.DoesNotContain("новый-456", journal);
    }

    [Fact]
    public async Task Text_read_before_the_last_key_is_marked_for_checking()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Чат");
        var box = window.Add("Сообщение", type: "Edit", hasValue: true);
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopTextInput(At(0), Target(window, box), "приве", ReadBeforeLastKey: true));
        monitor.Send(new DesktopKeyInput(At(1), Target(window, box), "Enter"));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal("приве", Text(steps[0]));
        Assert.Contains("последний символ мог не попасть", steps[0].Note);
        Assert.Equal("Enter", Text(steps[1], "keys"));
    }

    [Fact]
    public async Task Late_input_from_a_previous_recording_does_not_reach_the_new_one()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Блокнот");
        var ok = window.Add("OK");
        var (recorder, monitor) = await StartAsync();
        await recorder.StopAsync(CancellationToken.None);
        await recorder.StartAsync(CancellationToken.None);

        // Прошлый перехват опоздал с действием — оно не попадает в новую запись.
        monitor.Sinks[0](new DesktopClickInput(At(0), Target(window, ok), 500, 400, MouseButtonKind.Left));
        Assert.Empty(recorder.State.Steps);
        monitor.Sinks[1](new DesktopClickInput(At(1), Target(window, ok), 500, 400, MouseButtonKind.Left));
        Assert.Single(recorder.State.Steps);
        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Recorder_that_failed_to_start_is_not_left_recording()
    {
        var monitor = new FakeInputMonitor { StartFailure = new InvalidOperationException("хук не поставлен") };
        var recorder = new DesktopRecorder(monitor);
        var error = await Assert.ThrowsAsync<DesktopRecorderException>(() => recorder.StartAsync(CancellationToken.None));

        Assert.Equal("Не удалось начать запись: хук не поставлен", error.Message);
        Assert.False(recorder.State.Recording);
        monitor.StartFailure = null;
        await recorder.StartAsync(CancellationToken.None);
        Assert.True(recorder.State.Recording);
        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Steps_survive_a_stop_that_did_not_finish()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Блокнот");
        var ok = window.Add("OK");
        var (recorder, monitor) = await StartAsync();
        monitor.Send(new DesktopClickInput(At(0), Target(window, ok), 500, 400, MouseButtonKind.Left));
        monitor.StopFails = true;
        var step = Assert.Single(await recorder.StopAsync(CancellationToken.None));

        Assert.Contains("после этого шага: перехват не остановился как следует (перехват не ответил)", step.Note);
        Assert.False(recorder.State.Recording);
        // Новую запись можно начать.
        monitor.StopFails = false;
        await recorder.StartAsync(CancellationToken.None);
        Assert.Empty(recorder.State.Steps);
        await recorder.StopAsync(CancellationToken.None);
    }
}
