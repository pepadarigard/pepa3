using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Desktop.Tests;

/// <summary>Блоки окон Windows на имитации Windows и настоящем движке.</summary>
public sealed class DesktopTests
{
    private static async Task<(EngineHarness H, FakeDesktop D)> SetupAsync()
    {
        var desktop = new FakeDesktop();
        var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterDesktopNodes(desktop));
        return (h, desktop);
    }

    /// <summary>
    /// Остановившийся с ошибкой процесс держит мышь и клавиатуру, пока пользователь не решит, что делать; в тестах — остановить.
    /// </summary>
    private static async Task<ProcessRecord> FailedAsync(EngineHarness h, string id)
    {
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        await h.Engine.CancelAsync(id);
        return process;
    }

    private static ScenarioDefinition One(string type, object config) => new ScenarioBuilder("Окно")
        .Variable("value", VariableType.Any)
        .Node("start", "start").Node("act", type, config).Node("end", "end", new { message = "готово" })
        .Edge("start", "act").Edge("act", "end").Build();

    [Fact]
    public async Task Click_uses_UI_Automation_when_possible_and_mouse_otherwise()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        var window = d.AddWindow("Документ — Блокнот", "notepad");
        var save = window.Add("Сохранить");
        var legacy = window.Add("Старая кнопка", canInvoke: false);

        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.click", new { window = "блокнот", name = "Сохранить" })), ProcessStatus.Completed);
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.click", new { window = "блокнот", name = "Старая кнопка" })), ProcessStatus.Completed);
        Assert.Equal(1, save.Clicks);
        Assert.Equal(1, legacy.Clicks);
        Assert.Contains("invoke Сохранить", d.Actions);
        Assert.Contains("click Старая кнопка", d.Actions);
        // Перед нажатием мышью окно выводится на передний план.
        var actions = d.Actions.ToList();
        Assert.True(actions.IndexOf("activate Документ — Блокнот") < actions.IndexOf("click Старая кнопка"));
    }

    [Fact]
    public async Task Missing_window_ambiguous_or_disabled_element_is_not_clicked()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        var window = d.AddWindow("Отчёт");
        window.Add("Удалить");
        window.Add("Удалить");
        window.Add("Отправить", enabled: false);

        var missing = await h.RunAsync(One("desktop.click", new { window = "нет такого", name = "OK", waitSeconds = 1 }));
        Assert.Contains("не найдено", (await FailedAsync(h, missing)).StatusMessage);

        var ambiguous = await h.RunAsync(One("desktop.click", new { window = "Отчёт", name = "Удалить" }));
        Assert.Contains("подходит 2 элементов", (await FailedAsync(h, ambiguous)).StatusMessage);

        var disabled = await h.RunAsync(One("desktop.click", new { window = "Отчёт", name = "Отправить" }));
        Assert.Contains("недоступен", (await FailedAsync(h, disabled)).StatusMessage);
        Assert.All(window.Elements, e => Assert.Equal(0, e.Clicks));

        // Явный выбор «первый» снимает неоднозначность.
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.click", new { window = "Отчёт", name = "Удалить", match = "first" })),
            ProcessStatus.Completed);
        Assert.Equal(1, window.Elements[0].Clicks);
        Assert.Equal(0, window.Elements[1].Clicks);
    }

    [Fact]
    public async Task Two_windows_with_the_same_title_are_not_guessed()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        d.AddWindow("Без имени — Блокнот", "notepad").Add("OK");
        d.AddWindow("Без имени — Блокнот", "notepad").Add("OK");
        var id = await h.RunAsync(One("desktop.click", new { window = "Блокнот", name = "OK" }));
        Assert.Contains("несколько окон", (await FailedAsync(h, id)).StatusMessage);
    }

    [Fact]
    public async Task Fill_sets_value_and_after_unknown_outcome_verification_reads_the_field()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        var window = d.AddWindow("Вход");
        var login = window.Add("Логин", type: "Edit", hasValue: true);

        d.FailAfterAction = true;
        var id = await h.RunAsync(One("desktop.fill", new { window = "Вход", name = "Логин", text = "pepa" }));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("pepa", login.Value);
        Assert.Single(d.Actions, a => a.StartsWith("set ", StringComparison.Ordinal));
        Assert.Contains(await h.StepsAsync(id), s => s.NodeId == "act" && (s.Resolution ?? "").Contains("уже нужный текст", StringComparison.Ordinal));

        // Поле без Value pattern — клавиатурой: выделить всё и напечатать.
        d.FailAfterAction = false;
        window.Add("Пароль", type: "Edit");
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.fill", new { window = "Вход", name = "Пароль", text = "секрет" })), ProcessStatus.Completed);
        var actions = d.Actions.ToList();
        Assert.Equal(["focus Пароль", "keys Ctrl+A", "type секрет"], actions.SkipWhile(a => a != "focus Пароль").Take(3));
    }

    [Fact]
    public async Task Click_with_unknown_outcome_is_not_repeated_by_itself()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        var button = d.AddWindow("Банк").Add("Оплатить");
        d.FailAfterAction = true;
        var id = await h.RunAsync(One("desktop.click", new { window = "Банк", name = "Оплатить" }));
        var process = await FailedAsync(h, id);
        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Equal(1, button.Clicks);
    }

    [Fact]
    public async Task Keyboard_and_mouse_act_only_in_the_found_window()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        d.AddWindow("Редактор", x: 100, y: 200, width: 800, height: 600);

        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.keys", new { window = "Редактор", keys = "ctrl+shift+s" })), ProcessStatus.Completed);
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.keys", new { window = "Редактор", mode = "type", text = "Привет, mir" })), ProcessStatus.Completed);
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.mouse", new { window = "Редактор", x = 10, y = 20, @double = true })), ProcessStatus.Completed);
        var actions = d.Actions.ToList();
        Assert.Contains("keys Ctrl+Shift+S", actions);
        Assert.Contains("type Привет, mir", actions);
        Assert.Contains("mouse 110,220 double", actions);
        Assert.Equal(3, actions.Count(a => a == "activate Редактор"));

        var outside = await h.RunAsync(One("desktop.mouse", new { window = "Редактор", x = 900, y = 20 }));
        Assert.Contains("за пределами окна", (await FailedAsync(h, outside)).StatusMessage);

        var noWindow = await h.RunAsync(One("desktop.keys", new { window = "Нет такого", keys = "Enter", waitSeconds = 1 }));
        await FailedAsync(h, noWindow);
        Assert.DoesNotContain(d.Actions, a => a == "keys Enter");
    }

    [Fact]
    public async Task Wrong_key_combination_is_caught_by_scenario_check()
    {
        var (h, _) = await SetupAsync();
        await using var __ = h;
        var report = h.Engine.Validator.Validate(One("desktop.keys", new { window = "Окно", keys = "Ctrl+Привет" }));
        Assert.Contains(report.Issues, i => i.Field == "keys" && i.Message.Contains("неизвестна", StringComparison.Ordinal));
        report = h.Engine.Validator.Validate(One("desktop.keys", new { window = "Окно", keys = "S+Ctrl" }));
        Assert.Contains(report.Issues, i => i.Field == "keys" && i.Message.Contains("последней", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Ctrl+S", "Ctrl,S")]
    [InlineData("control + shift + f5", "Ctrl,Shift,F5")]
    [InlineData("Enter", "Enter")]
    [InlineData("alt+F4", "Alt,F4")]
    [InlineData("Win+d", "Win,D")]
    [InlineData("Esc", "Escape")]
    public void Key_combinations_are_normalized(string combo, string expected) =>
        Assert.Equal(expected.Split(','), KeyCombo.Parse(combo));

    [Fact]
    public async Task Input_blocks_take_mouse_and_keyboard_so_two_processes_never_mix_input()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        d.AddWindow("Редактор");
        var scenario = new ScenarioBuilder("Ввод и пауза")
            .Node("start", "start")
            .Node("type", "desktop.keys", new { window = "Редактор", mode = "type", text = "A" })
            .Node("wait", "wait", new { amount = 60, unit = "seconds" })
            .Node("end", "end")
            .Chain("start", "type", "wait", "end").Build();
        var first = await h.RunAsync(scenario);
        await h.WaitForStatusAsync(first, ProcessStatus.Waiting);
        var second = await h.RunAsync(scenario);
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Single(d.Actions, a => a == "type A");

        // Первый закончил — ресурс освобождается, второй печатает.
        await h.WaitForStatusAsync(first, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(5));
        await h.WaitForStatusAsync(second, ProcessStatus.Waiting);
        Assert.Equal(2, d.Actions.Count(a => a == "type A"));
    }

    [Fact]
    public async Task Start_program_waits_for_window_and_is_verified_by_it()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        d.AddWindow("Калькулятор", "calc");
        var id = await h.RunAsync(One("desktop.start", new { path = "calc", window = "Калькулятор" }));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(["calc"], d.Started);

        var missing = await h.RunAsync(One("desktop.start", new { path = @"C:\нет\такой.exe" }));
        Assert.Contains("файл не найден", (await FailedAsync(h, missing)).StatusMessage);

        var node = new StartProgramNode(d);
        var context = Context(new() { ["path"] = "calc", ["window"] = "Калькулятор" });
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, default)).Outcome);
    }

    [Fact]
    public async Task Close_window_is_idempotent_and_reports_a_window_that_refuses()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        d.AddWindow("Готово");
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.close", new { window = "Готово" })), ProcessStatus.Completed);
        // Окна уже нет — блок ничего не делает и не считает это ошибкой.
        await h.WaitForStatusAsync(await h.RunAsync(One("desktop.close", new { window = "Готово" })), ProcessStatus.Completed);
        Assert.Single(d.Actions, a => a == "close Готово");

        d.AddWindow("Несохранённый документ").RefusesToClose = true;
        var refused = await h.RunAsync(One("desktop.close", new { window = "Несохранённый", waitSeconds = 1 }));
        Assert.Contains("не закрылось", (await FailedAsync(h, refused)).StatusMessage);
    }

    [Fact]
    public async Task Read_and_wait_for_elements()
    {
        var (h, d) = await SetupAsync();
        await using var _ = h;
        var window = d.AddWindow("Загрузка");
        window.Add("Статус", type: "Text").Value = "Готово: 3 файла";
        var read = await h.RunAsync(One("desktop.read", new { window = "Загрузка", name = "Статус", saveTo = "value" }));
        Assert.Equal("Готово: 3 файла", (await h.WaitForStatusAsync(read, ProcessStatus.Completed)).Variables["value"]!.GetValue<string>());

        var exists = await h.RunAsync(One("desktop.read", new { window = "Загрузка", name = "Ошибка", what = "exists", saveTo = "value", waitSeconds = 1 }));
        Assert.False((await h.WaitForStatusAsync(exists, ProcessStatus.Completed)).Variables["value"]!.GetValue<bool>());

        var waiting = await h.RunAsync(new ScenarioBuilder("Ждать")
            .Node("start", "start")
            .Node("wait", "desktop.waitFor", new { window = "Загрузка", name = "Закрыть", waitSeconds = 10 })
            .Node("end", "end", new { message = "появилась" })
            .Node("late", "end", new { message = "нет" })
            .Edge("start", "wait").Edge("wait", "end").Edge("wait", "late", Ports.Timeout).Build());
        await Task.Delay(500);
        window.Add("Закрыть");
        Assert.Equal("появилась", (await h.WaitForStatusAsync(waiting, ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Without_Windows_blocks_explain_why_they_do_not_work()
    {
        var desktop = new UnavailableDesktop("Окна Windows доступны только в приложении Pepa для Windows.");
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterDesktopNodes(desktop));
        var id = await h.RunAsync(One("desktop.click", new { window = "Блокнот", name = "OK" }));
        Assert.Contains("только в приложении Pepa для Windows", (await FailedAsync(h, id)).StatusMessage);
    }

    private static NodeExecutionContext Context(System.Text.Json.Nodes.JsonObject input) => new()
    {
        ProcessId = "p1",
        Node = new NodeDefinition { Id = "n", Type = "test" },
        Input = input,
        Variables = new System.Text.Json.Nodes.JsonObject(),
        Attempt = 1,
        Time = TimeProvider.System,
    };
}
