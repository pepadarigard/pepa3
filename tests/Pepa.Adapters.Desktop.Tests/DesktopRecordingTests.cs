using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Desktop.Tests;

/// <summary>Запись действий в окнах Windows: действия пользователя → блоки, которые потом выполняются на тех же окнах.</summary>
public sealed class DesktopRecordingTests
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

    private static ScenarioDefinition Scenario(IReadOnlyList<RecordedDesktopStep> steps)
    {
        var builder = new ScenarioBuilder("Записано").Variable("password", VariableType.Text, isInput: true).Node("start", "start");
        var ids = new List<string> { "start" };
        for (var i = 0; i < steps.Count; i++)
        {
            builder.Node($"rec{i + 1}", steps[i].Type, steps[i].Config, label: steps[i].Label);
            ids.Add($"rec{i + 1}");
        }

        builder.Node("end", "end", new { message = "готово" });
        ids.Add("end");
        return builder.Chain([.. ids]).Build();
    }

    [Fact]
    public async Task Clicks_typing_and_keys_become_blocks_that_repeat_the_actions_in_the_same_window()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Отчёт.txt — Блокнот", "notepad");
        var name = window.Add("Имя", type: "Edit", hasValue: true);
        var save = window.Add("Сохранить", id: "save");
        var file = window.Add("Отчёт", type: "ListItem");
        var list = window.Add("Список", type: "ListItem");
        var password = window.Add("Пароль", type: "Edit", hasValue: true);
        var monitor = new FakeInputMonitor();
        var recorder = new DesktopRecorder(monitor);

        await recorder.StartAsync(CancellationToken.None);
        Assert.True(recorder.State.Recording);
        // Нажатие в поле и ввод — один шаг «Ввести»: блок сам ставит курсор в поле.
        monitor.Send(new DesktopClickInput(At(0), Target(window, name), 120, 220, MouseButtonKind.Left));
        monitor.Send(new DesktopTextInput(At(1), Target(window, name), "Иван"));
        monitor.Send(new DesktopKeyInput(At(2), Target(window, name), "Enter"));
        monitor.Send(new DesktopClickInput(At(3), Target(window, save, sameId: 1), 130, 230, MouseButtonKind.Left));
        // Два быстрых нажатия в одну точку — двойной щелчок.
        monitor.Send(new DesktopClickInput(At(4), Target(window, file), 300, 400, MouseButtonKind.Left));
        monitor.Send(new DesktopClickInput(At(4.2), Target(window, file), 301, 401, MouseButtonKind.Left));
        // Элемент не определился — нажатие в точке окна (окно в 100, 200).
        monitor.Send(new DesktopClickInput(At(6), Target(window, null), 150, 260, MouseButtonKind.Left));
        monitor.Send(new DesktopSkippedInput(At(7), "перетаскивание мышью не записывается"));
        monitor.Send(new DesktopKeyInput(At(8), Target(window, null), "Ctrl+S"));
        monitor.Send(new DesktopClickInput(At(9), Target(window, list), 140, 240, MouseButtonKind.Right));
        // Пароль не читается и не записывается.
        monitor.Send(new DesktopTextInput(At(10), Target(window, password, password: true), null));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.False(recorder.State.Recording);
        Assert.False(monitor.Watching);
        Assert.Equal(["desktop.fill", "desktop.keys", "desktop.click", "desktop.click", "desktop.mouse", "desktop.keys", "desktop.click", "desktop.fill"],
            steps.Select(s => s.Type));
        Assert.All(steps, s => Assert.Equal("Блокнот", s.Config["window"]!.GetValue<string>()));
        Assert.All(steps, s => Assert.Equal("notepad", s.Config["process"]!.GetValue<string>()));
        Assert.Contains("сокращён до «Блокнот»", steps[0].Note);
        Assert.Equal("Иван", steps[0].Config["text"]!.GetValue<string>());
        Assert.Equal("Edit", steps[0].Config["controlType"]!.GetValue<string>());
        Assert.Equal("Enter", steps[1].Config["keys"]!.GetValue<string>());
        // Уникальный код — в поиске; подпись — проверка, что это тот же элемент.
        Assert.Equal("save", steps[2].Config["automationId"]!.GetValue<string>());
        Assert.Equal("Сохранить", steps[2].Config["name"]!.GetValue<string>());
        Assert.Equal("Нажать «Сохранить»", steps[2].Label);
        Assert.Null(steps[2].Note);
        Assert.Equal("double", steps[3].Config["how"]!.GetValue<string>());
        Assert.Equal(50, steps[4].Config["x"]!.GetValue<int>());
        Assert.Equal(60, steps[4].Config["y"]!.GetValue<int>());
        Assert.Contains("элемент не определился", steps[4].Note);
        Assert.StartsWith("перед этим шагом: перетаскивание", steps[5].Note);
        Assert.Equal("right", steps[6].Config["how"]!.GetValue<string>());
        Assert.True(steps[7].Secret);
        Assert.Equal("{{ vars.password }}", steps[7].Config["text"]!.GetValue<string>());

        // Записанные блоки выполняются на тех же окнах и повторяют действия.
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterDesktopNodes(d));
        var id = await h.RunAsync(Scenario(steps), new JsonObject { ["password"] = "секрет" });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var actions = d.Actions.Where(a => !a.StartsWith("activate", StringComparison.Ordinal) && !a.StartsWith("focus", StringComparison.Ordinal)).ToList();
        Assert.Equal(["set Имя=Иван", "keys Enter", "invoke Сохранить", "double Отчёт", "mouse 150,260", "keys Ctrl+S", "right Список", "set Пароль=секрет"],
            actions);
    }

    [Fact]
    public async Task Element_that_is_not_unique_gets_a_warning_and_the_block_stops_instead_of_guessing()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Отчёт");
        var ok = window.Add("OK");
        window.Add("OK");
        var monitor = new FakeInputMonitor();
        var recorder = new DesktopRecorder(monitor);
        await recorder.StartAsync(CancellationToken.None);
        monitor.Send(new DesktopClickInput(At(0), Target(window, ok, sameName: 2), 110, 210, MouseButtonKind.Left));
        var step = Assert.Single(await recorder.StopAsync(CancellationToken.None));

        Assert.Contains("в окне 2 элемента с такой подписью", step.Note);
        Assert.Equal("strict", step.Config["match"]!.GetValue<string>());
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterDesktopNodes(d));
        var id = await h.RunAsync(Scenario([step]));
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("подходит 2 элементов", stopped.StatusMessage);
        Assert.Equal(0, ok.Clicks);
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Multiline_text_and_retyping_are_one_step_but_enter_that_did_something_stays()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Заметки");
        var note = window.Add("Текст", type: "Edit", hasValue: true);
        var search = window.Add("Поиск", type: "Edit", hasValue: true);
        var monitor = new FakeInputMonitor();
        var recorder = new DesktopRecorder(monitor);
        await recorder.StartAsync(CancellationToken.None);

        // Enter стал частью текста (поле многострочное) — один шаг с итоговым текстом.
        monitor.Send(new DesktopTextInput(At(0), Target(window, note), "строка 1"));
        monitor.Send(new DesktopKeyInput(At(1), Target(window, note), "Enter"));
        monitor.Send(new DesktopTextInput(At(2), Target(window, note), "строка 1\nстрока 2"));
        // Enter запустил поиск (текст без перевода строки) — он остаётся шагом.
        monitor.Send(new DesktopTextInput(At(3), Target(window, search), "pepa"));
        monitor.Send(new DesktopKeyInput(At(4), Target(window, search), "Enter"));
        monitor.Send(new DesktopTextInput(At(5), Target(window, search), "pepa 2"));
        // Набор, не законченный к остановке, перехватчик передаёт при остановке.
        monitor.OnStop.Add(new DesktopTextInput(At(6), Target(window, search), "pepa 3"));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(["desktop.fill", "desktop.fill", "desktop.keys", "desktop.fill"], steps.Select(s => s.Type));
        Assert.Equal("строка 1\nстрока 2", steps[0].Config["text"]!.GetValue<string>());
        Assert.Equal("pepa", steps[1].Config["text"]!.GetValue<string>());
        Assert.Equal("pepa 3", steps[3].Config["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Field_without_name_or_code_is_typed_where_the_cursor_is_and_unread_text_is_reported()
    {
        var d = new FakeDesktop();
        var window = d.AddWindow("Форма");
        var blank = window.Add("", type: "Edit");
        var field = window.Add("Комментарий", type: "Edit");
        var monitor = new FakeInputMonitor();
        var recorder = new DesktopRecorder(monitor);
        await recorder.StartAsync(CancellationToken.None);
        monitor.Send(new DesktopTextInput(At(0), Target(window, blank, sameName: 0), "текст"));
        monitor.Send(new DesktopTextInput(At(1), Target(window, field), null));
        var steps = await recorder.StopAsync(CancellationToken.None);

        Assert.Equal("desktop.keys", steps[0].Type);
        Assert.Equal("type", steps[0].Config["mode"]!.GetValue<string>());
        Assert.Equal("текст", steps[0].Config["text"]!.GetValue<string>());
        Assert.Contains("туда, где стоит курсор", steps[0].Note);
        Assert.Contains("не смогла прочитать", steps[1].Note);
    }

    [Fact]
    public async Task Without_Windows_recording_explains_why_and_a_second_start_is_refused()
    {
        var unavailable = new DesktopRecorder(new UnavailableInputMonitor("только в Pepa.exe"));
        var error = await Assert.ThrowsAsync<DesktopRecorderException>(() => unavailable.StartAsync(CancellationToken.None));
        Assert.Equal("только в Pepa.exe", error.Message);
        Assert.False(unavailable.State.Recording);

        var recorder = new DesktopRecorder(new FakeInputMonitor());
        await recorder.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DesktopRecorderException>(() => recorder.StartAsync(CancellationToken.None));
        await recorder.StopAsync(CancellationToken.None);
        // Остановка без записи ничего не ломает.
        Assert.Empty(await recorder.StopAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0x41, false, false, false, false, true, false, "typing", null)] // буква в поле
    [InlineData(0x41, false, false, false, false, false, false, "key", "A")] // буква вне поля (переход по списку, меню)
    [InlineData(0x0D, false, false, false, false, true, false, "key", "Enter")] // Enter в однострочном поле — действие
    [InlineData(0x0D, false, false, false, false, true, true, "typing", null)] // Enter в многострочном — часть текста
    [InlineData(0x56, true, false, false, false, true, false, "typing", null)] // Ctrl+V в поле — изменит текст поля
    [InlineData(0x53, true, false, false, false, true, false, "key", "Ctrl+S")] // Ctrl+S в поле — команда программы
    [InlineData(0x32, true, true, false, false, true, false, "typing", null)] // AltGr+2 — символ
    [InlineData(0x73, false, true, false, false, false, false, "key", "Alt+F4")]
    [InlineData(0x09, false, true, false, false, false, false, "ignore", null)] // Alt+Tab — переключение окон
    [InlineData(0x45, false, false, false, true, false, false, "ignore", null)] // Win+E
    [InlineData(0xBE, false, false, false, false, false, false, "ignore", null)] // точка вне поля
    [InlineData(0x12, false, false, false, false, false, false, "key", "Alt")] // отдельное нажатие Alt — меню
    [InlineData(0x10, false, false, false, false, true, false, "ignore", null)] // Shift сам по себе
    [InlineData(0x09, false, false, true, false, true, false, "key", "Shift+Tab")]
    public void Keys_are_split_into_typing_and_key_steps(int vk, bool ctrl, bool alt, bool shift, bool win, bool editable, bool multiLine,
        string role, string? keys)
    {
        var decision = DesktopKeyRules.Classify(vk, ctrl, alt, shift, win, editable, multiLine);
        Assert.Equal(role, decision.Role.ToString().ToLowerInvariant());
        Assert.Equal(keys, decision.Keys);
        if (decision.Keys is { } combo)
        {
            // Записанное сочетание понимает блок «Клавиатура».
            Assert.Equal(combo, string.Join("+", KeyCombo.Parse(combo)));
        }
    }

    [Theory]
    [InlineData("Безымянный – Блокнот", "Блокнот", true)]
    [InlineData("C:\\папка - копия\\отчёт.txt - Notepad++", "Notepad++", true)]
    [InlineData("Калькулятор", "Калькулятор", false)]
    [InlineData("Сохранить как", "Сохранить как", false)]
    public void Window_title_is_shortened_to_the_program_part(string title, string expected, bool shortened) =>
        Assert.Equal((expected, shortened), DesktopRecorder.TitlePart(title));
}
