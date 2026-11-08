using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>«Выбор варианта» (D-083): ветвление по ответу без JavaScript, неожиданное — остановка, а не случайная ветка.</summary>
public sealed class ChoiceTests
{
    private static ScenarioDefinition Choose(string match = "exact", bool connectOther = true, string option2 = "нет; отмена") =>
        Build(match, connectOther, option2).Build();

    private static ScenarioBuilder Build(string match, bool connectOther, string option2)
    {
        var builder = new ScenarioBuilder("Ответ")
            .Variable("answer", VariableType.Text, "", isInput: true)
            .Variable("picked", VariableType.Any)
            .Node("start", "start")
            .Node("choose", "choice", new { value = "{{ vars.answer }}", match, option1 = "готово", option2, saveTo = "picked" })
            .Node("yes", "end", new { message = "готово: {{ vars.picked.text }}" })
            .Node("no", "end", new { outcome = "failure", message = "отказ" })
            .Edge("start", "choose")
            .Edge("choose", "yes", "v1")
            .Edge("choose", "no", "v2");
        if (connectOther)
        {
            builder.Node("other", "end", new { message = "другое: {{ vars.picked.value }}" }).Edge("choose", "other", "other");
        }

        return builder;
    }

    private static async Task<ProcessRecord> RunAsync(EngineHarness h, ScenarioDefinition scenario, string answer, ProcessStatus expected)
    {
        var id = await h.RunAsync(scenario, new JsonObject { ["answer"] = answer });
        return await h.WaitForStatusAsync(id, expected);
    }

    [Fact]
    public async Task Each_answer_goes_to_its_option()
    {
        await using var h = await EngineHarness.CreateAsync();
        Assert.Equal("готово: готово", (await RunAsync(h, Choose(), "Готово!", ProcessStatus.Completed)).StatusMessage);
        Assert.Equal("отказ", (await RunAsync(h, Choose(), "  Отмена 🙅", ProcessStatus.Failed)).StatusMessage);
        Assert.Equal("другое: ещё не готово", (await RunAsync(h, Choose(), "ещё не готово", ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Unexpected_answer_stops_the_process_when_other_is_not_connected()
    {
        await using var h = await EngineHarness.CreateAsync();
        var stopped = await RunAsync(h, Choose(connectOther: false), "минутку", ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Contains("«минутку» не подошло ни к одному варианту", stopped.StatusMessage);
        Assert.Contains("Выход «Другое» не подключён", stopped.StatusMessage);
    }

    [Fact]
    public async Task Answer_that_fits_two_options_stops_instead_of_guessing()
    {
        await using var h = await EngineHarness.CreateAsync();
        // «Слово целиком»: «не готово» содержит и «готово», и «не готово».
        var stopped = await RunAsync(h, Choose(match: "word", option2: "не готово"), "Пока не готово", ProcessStatus.NeedsAttention);
        Assert.Contains("подходит сразу к нескольким вариантам: «готово», «не готово»", stopped.StatusMessage);
    }

    [Fact]
    public async Task Choice_is_saved_and_starts_with_mode_takes_leading_words()
    {
        await using var h = await EngineHarness.CreateAsync();
        var done = await RunAsync(h, Choose(match: "starts"), "Готово, спасибо", ProcessStatus.Completed);
        Assert.Equal("готово: готово", done.StatusMessage);
        Assert.Equal(1, done.Variables["picked"]!["option"]!.GetValue<int>());
        Assert.Equal("Готово, спасибо", done.Variables["picked"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Validator_rejects_an_edge_from_an_empty_option_and_warns_about_an_unconnected_option()
    {
        await using var h = await EngineHarness.CreateAsync();
        var dangling = Build("exact", true, "нет")
            .Node("ghost", "end").Edge("choose", "ghost", "v3")
            .Build();
        var report = h.Engine.Validator.Validate(dangling);
        Assert.Contains(report.Issues, i => i.Code == "edge.port.empty" && i.Message.Contains("«Вариант 3»", StringComparison.Ordinal));

        var unconnected = new ScenarioBuilder("Ответ")
            .Node("start", "start")
            .Node("choose", "choice", new { value = "x", option1 = "готово", option2 = "нет" })
            .Node("yes", "end")
            .Edge("start", "choose").Edge("choose", "yes", "v1")
            .Build();
        var warnings = h.Engine.Validator.Validate(unconnected);
        Assert.True(warnings.IsValid);
        Assert.Contains(warnings.Issues, i => i.Code == "port.option.unconnected" && i.Message.Contains("«нет»", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Symbol_answers_match_and_a_secret_with_odd_spaces_is_hidden_in_the_step_text()
    {
        await using var h = await EngineHarness.CreateAsync();
        var plus = await RunAsync(h, Choose(option2: "+; 👍"), "👍", ProcessStatus.Failed);
        Assert.Equal("отказ", plus.StatusMessage);

        // Секрет с неразрывным пробелом и переносом строки: в пояснении шага он скрыт, хотя пробелы там склеиваются.
        var id = await h.RunAsync(new ScenarioBuilder("Секрет")
            .Variable("answer", VariableType.Text, "pass\u00A0word\nline2", secret: true)
            .Node("start", "start")
            .Node("choose", "choice", new { value = "{{ vars.answer }}", option1 = "готово" })
            .Node("yes", "end")
            .Edge("start", "choose").Edge("choose", "yes", "v1")
            .Build());
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.DoesNotContain("pass", stopped.StatusMessage);
        Assert.DoesNotContain("line2", stopped.StatusMessage);
        Assert.All(await h.Store.GetJournalAsync(id), j => Assert.DoesNotContain("line2", j.Message));
    }
}
