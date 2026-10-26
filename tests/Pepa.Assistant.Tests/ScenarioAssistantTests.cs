using System.Text.Json.Nodes;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;
using Pepa.Engine.Nodes;

namespace Pepa.Assistant.Tests;

/// <summary>Помощник с настоящим SDK Anthropic и имитацией сервера: запрос, разбор, проверка, исправление, ошибки.</summary>
public sealed class ScenarioAssistantTests : IDisposable
{
    private readonly FakeAnthropic _api = new();

    public void Dispose() => _api.Dispose();

    private static NodeRegistry Registry() => new NodeRegistry().RegisterLogicNodes().RegisterFlowNodes();

    private ScenarioAssistant Assistant(int maxRepairs = 2, int maxRetries = 0)
    {
        var registry = Registry();
        return new ScenarioAssistant(registry, new ScenarioValidator(registry, new JintExpressionEvaluator()), [Example()])
        {
            BaseUrl = _api.Url,
            MaxRetries = maxRetries,
            RetryDelay = TimeSpan.FromMilliseconds(10),
            MaxRepairs = maxRepairs,
        };
    }

    private static ScenarioDefinition Example() => new()
    {
        Name = "Пример",
        Description = "Пишет в журнал и заканчивается.",
        Nodes =
        [
            new NodeDefinition { Id = "start", Type = "start" },
            new NodeDefinition { Id = "log", Type = "log", Config = new JsonObject { ["message"] = "Привет" } },
            new NodeDefinition { Id = "end", Type = "end" },
        ],
        Edges =
        [
            new EdgeDefinition { Id = "a", Source = "start", Target = "log" },
            new EdgeDefinition { Id = "b", Source = "log", Target = "end" },
        ],
    };

    private static JsonObject Block(string id, string type, params (string Field, string Value)[] settings) => new()
    {
        ["id"] = id,
        ["type"] = type,
        ["label"] = "",
        ["note"] = "",
        ["settings"] = new JsonArray(settings.Select(s => (JsonNode)new JsonObject { ["field"] = s.Field, ["value"] = s.Value }).ToArray()),
    };

    private static JsonObject Link(string from, string port, string to) => new() { ["from"] = from, ["port"] = port, ["to"] = to };

    private static JsonObject Draft(JsonObject[] blocks, JsonObject[] links, JsonObject[]? variables = null, string[]? questions = null) => new()
    {
        ["summary"] = "Проверяет число и пишет в журнал.",
        ["questions"] = new JsonArray((questions ?? []).Select(q => (JsonNode)q).ToArray()),
        ["variables"] = new JsonArray((variables ?? []).Select(v => (JsonNode)v).ToArray()),
        ["blocks"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()),
        ["links"] = new JsonArray(links.Select(l => (JsonNode)l).ToArray()),
    };

    private static JsonObject CountVariable() => new()
    {
        ["name"] = "count", ["type"] = "number", ["description"] = "Сколько", ["isInput"] = true, ["default"] = "3",
    };

    private static JsonObject ValidDraft() => Draft(
        [
            Block("check", "condition", ("expression", "vars.count > 2")),
            Block("many", "log", ("message", "Много: {{ vars.count }}")),
            Block("few", "log", ("message", "Мало")),
            Block("done", "end"),
            Block("small", "end", ("outcome", "failure"), ("message", "Мало")),
        ],
        [
            Link("check", "true", "many"), Link("check", "false", "few"),
            Link("many", "out", "done"), Link("few", "out", "small"),
        ],
        [CountVariable()],
        ["Какое число считать большим?"]);

    private static string UserText(JsonNode body) => body["messages"]![0]!["content"] is JsonValue text
        ? text.GetValue<string>()
        : string.Concat(body["messages"]![0]!["content"]!.AsArray().Select(b => b!["text"]?.GetValue<string>()));

    private static AssistantRequest Ask(string text = "Если count больше 2 — написать «много», иначе «мало».") => new() { Description = text };

    [Fact]
    public async Task Draft_becomes_blocks_and_the_request_uses_schema_cache_and_fallback()
    {
        _api.ReplyDraft(ValidDraft());
        var draft = await Assistant().DraftAsync("sk-ant-test", Ask(), TestContext.Current.CancellationToken);

        Assert.Empty(draft.Problems);
        Assert.Equal(1, draft.Requests);
        Assert.Equal("check", draft.EntryNodeId);
        Assert.Equal(["Какое число считать большим?"], draft.Questions);
        Assert.Equal(5, draft.Nodes.Count);
        Assert.Equal(4, draft.Edges.Count);
        // Поля, которых модель не указала, — по умолчанию, как у блока, добавленного вручную.
        Assert.Equal("info", draft.Nodes.Single(n => n.Id == "many").Config["level"]!.GetValue<string>());
        var count = Assert.Single(draft.Variables);
        Assert.Equal((VariableType.Number, true, 3d), (count.Type, count.IsInput, count.Default!.GetValue<double>()));
        // Расстановка: шаг за шагом слева направо, ветви — одна под другой.
        var position = draft.Nodes.ToDictionary(n => n.Id, n => n.Position);
        Assert.Equal(0, position["check"].X);
        Assert.Equal(position["many"].X, position["few"].X);
        Assert.NotEqual(position["many"].Y, position["few"].Y);

        var (path, headers, body) = Assert.Single(_api.Requests);
        Assert.Equal("/v1/messages", path);
        Assert.Equal("Bearer sk-ant-test", headers["authorization"]);
        Assert.Contains("server-side-fallback-2026-07-01", headers["anthropic-beta"]);
        Assert.Equal(ScenarioAssistant.DefaultModel, body!["model"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        var types = body["output_config"]!["format"]!["schema"]!["properties"]!["blocks"]!["items"]!["properties"]!["type"]!["enum"]!.AsArray()
            .Select(t => t!.GetValue<string>()).ToList();
        Assert.Contains("log", types);
        Assert.DoesNotContain("start", types);
        Assert.Equal("ephemeral", body["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Contains("### condition", body["system"]![0]!["text"]!.GetValue<string>());
        Assert.Contains("count больше 2", UserText(body));
    }

    [Fact]
    public async Task Validation_errors_are_sent_back_for_repair()
    {
        // Первый черновик: у условия нет выражения, у журнала нет такого поля, выход «нет» никуда не ведёт.
        _api.ReplyDraft(Draft(
            [Block("check", "condition"), Block("many", "log", ("message", "Много"), ("colour", "red")), Block("done", "end")],
            [Link("check", "true", "many"), Link("many", "out", "done")]));
        _api.ReplyDraft(ValidDraft());

        var draft = await Assistant().DraftAsync("sk-ant-test", Ask(), TestContext.Current.CancellationToken);
        Assert.Empty(draft.Problems);
        Assert.Equal(2, draft.Requests);
        Assert.Equal(new AssistantUsage(2400, 1600, 9000), draft.Usage);

        var repair = UserText(_api.Requests.ElementAt(1).Body!);
        Assert.Contains("colour", repair);
        Assert.Contains("<ошибки>", repair);
        Assert.Contains("\"id\":\"check\"", repair);
    }

    [Fact]
    public async Task Draft_with_wrong_structure_is_checked_and_problems_left_after_repairs_are_returned()
    {
        // Ошибки разбора и проверки: два первых блока, связь из несуществующего выхода, неизвестный тип.
        var bad = Draft(
            [Block("a", "log", ("message", "1")), Block("b", "log", ("message", "2")), Block("c", "log", ("message", "3"), ("level", "громко"))],
            [Link("a", "yes", "c"), Link("b", "out", "nowhere")]);
        _api.ReplyDraft(bad);
        _api.ReplyDraft(bad);

        var draft = await Assistant(maxRepairs: 1).DraftAsync("sk-ant-test", Ask(), TestContext.Current.CancellationToken);
        Assert.Equal(2, draft.Requests);
        Assert.Contains(draft.Problems, p => p.Contains("нет выхода «yes»", StringComparison.Ordinal));
        Assert.Contains(draft.Problems, p => p.Contains("блока «nowhere» нет", StringComparison.Ordinal));
        Assert.Contains(draft.Problems, p => p.Contains("«громко» не подходит", StringComparison.Ordinal));
        // Черновик всё равно можно вставить и поправить вручную.
        Assert.Equal(3, draft.Nodes.Count);
    }

    [Fact]
    public async Task Infinite_numbers_from_the_model_are_problems_not_a_crash()
    {
        var odd = Draft(
            [Block("pause", "wait", ("amount", "Infinity"), ("unit", "seconds")), Block("done", "end")],
            [Link("pause", "out", "done")],
            [new JsonObject { ["name"] = "count", ["type"] = "number", ["description"] = "Сколько", ["isInput"] = false, ["default"] = "1e999" }]);
        _api.ReplyDraft(odd);

        var draft = await Assistant(maxRepairs: 0).DraftAsync("sk-ant-test", Ask(), TestContext.Current.CancellationToken);
        Assert.Contains(draft.Problems, p => p.Contains("Infinity", StringComparison.Ordinal));
        Assert.Null(Assert.Single(draft.Variables).Default);
    }

    [Fact]
    public async Task Existing_variables_are_not_declared_again_and_are_described_to_the_model()
    {
        _api.ReplyDraft(ValidDraft());
        var existing = new VariableDefinition { Name = "count", Type = VariableType.Number, Description = "Число из формы" };
        var draft = await Assistant().DraftAsync("sk-ant-test", Ask() with { ExistingVariables = [existing] },
            TestContext.Current.CancellationToken);
        Assert.Empty(draft.Variables);
        Assert.Empty(draft.Problems);
        Assert.Contains("Число из формы", UserText(_api.Requests.Single().Body!));
    }

    /// <summary>Сценарий из черновика ValidDraft, уже стоящий на схеме: «Начало» → check → …</summary>
    private static ScenarioDefinition Current() => new()
    {
        Name = "Проверка числа",
        Variables =
        [
            new VariableDefinition { Name = "count", Type = VariableType.Number, Description = "Сколько", IsInput = true, Default = JsonValue.Create(777123) },
        ],
        Nodes =
        [
            new NodeDefinition { Id = "start", Type = "start", Position = new NodePosition(0, 0) },
            new NodeDefinition { Id = "check", Type = "condition", Position = new NodePosition(300, 0), Config = new JsonObject { ["expression"] = "vars.count > 2" } },
            new NodeDefinition { Id = "many", Type = "log", Position = new NodePosition(600, 0), Config = new JsonObject { ["message"] = "Много", ["level"] = "info" } },
            new NodeDefinition { Id = "few", Type = "log", Position = new NodePosition(600, 150), Config = new JsonObject { ["message"] = "Мало", ["level"] = "info" } },
            new NodeDefinition { Id = "done", Type = "end", Position = new NodePosition(900, 0), Config = new JsonObject { ["outcome"] = "success" } },
            new NodeDefinition { Id = "small", Type = "end", Position = new NodePosition(900, 150), Config = new JsonObject { ["outcome"] = "failure", ["message"] = "Мало" } },
        ],
        Edges =
        [
            new EdgeDefinition { Id = "s", Source = "start", SourcePort = "out", Target = "check" },
            new EdgeDefinition { Id = "a", Source = "check", SourcePort = "true", Target = "many" },
            new EdgeDefinition { Id = "b", Source = "check", SourcePort = "false", Target = "few" },
            new EdgeDefinition { Id = "c", Source = "many", SourcePort = "out", Target = "done" },
            new EdgeDefinition { Id = "d", Source = "few", SourcePort = "out", Target = "small" },
        ],
    };

    [Fact]
    public async Task Existing_scenario_is_changed_keeping_blocks_and_their_places()
    {
        // Просьба: после «Много» подождать минуту. Модель возвращает весь сценарий с новым блоком.
        _api.ReplyDraft(Draft(
            [
                Block("check", "condition", ("expression", "vars.count > 2")),
                Block("many", "log", ("message", "Много")),
                Block("pause", "wait", ("amount", "1"), ("unit", "minutes")),
                Block("few", "log", ("message", "Мало")),
                Block("done", "end"),
                Block("small", "end", ("outcome", "failure"), ("message", "Мало")),
            ],
            [
                Link("check", "true", "many"), Link("check", "false", "few"),
                Link("many", "out", "pause"), Link("pause", "out", "done"), Link("few", "out", "small"),
            ]));
        var current = Current();
        var draft = await Assistant().DraftAsync("sk-ant-test",
            Ask("После «Много» подождать минуту.") with { ExistingVariables = current.Variables, Current = current },
            TestContext.Current.CancellationToken);

        Assert.Empty(draft.Problems);
        Assert.Equal("check", draft.EntryNodeId);
        var position = draft.Nodes.ToDictionary(n => n.Id, n => n.Position);
        Assert.Equal(new NodePosition(600, 0), position["many"]);
        Assert.Equal(new NodePosition(900, 0), position["done"]);
        // Новый блок — справа от того, за которым идёт, ниже, если место занято.
        Assert.True(position["pause"].X > position["many"].X);
        Assert.NotEqual(0, position["pause"].Y);

        var text = UserText(_api.Requests.Single().Body!);
        Assert.Contains("<сценарий>", text);
        Assert.Contains("«check»", text);
        Assert.Contains("vars.count > 2", text);
        // Значения переменных помощнику не отправляются.
        Assert.DoesNotContain("777123", text);
    }

    [Fact]
    public async Task Scenario_is_explained_by_blocks_without_changes()
    {
        _api.ReplyDraft(new JsonObject
        {
            ["summary"] = "Проверяет число и пишет в журнал.",
            ["steps"] = new JsonArray(
                new JsonObject { ["block"] = "check", ["text"] = "Сравнивает count с двойкой." },
                new JsonObject { ["block"] = "nowhere", ["text"] = "Блок, которого нет." }),
            ["cautions"] = new JsonArray("Если count не задан, условие будет ложным."),
        });
        var explanation = await Assistant().ExplainAsync("sk-ant-test", Current(), TestContext.Current.CancellationToken);

        Assert.Equal("Проверяет число и пишет в журнал.", explanation.Summary);
        Assert.Equal([new ExplainedBlock("check", "Сравнивает count с двойкой."), new ExplainedBlock(null, "Блок, которого нет.")], explanation.Steps);
        Assert.Single(explanation.Cautions);
        var body = _api.Requests.Single().Body!;
        Assert.NotNull(body["output_config"]!["format"]!["schema"]!["properties"]!["cautions"]);
        Assert.Contains("Объясни", UserText(body));

        var empty = new ScenarioDefinition { Name = "Пусто", Nodes = [new NodeDefinition { Id = "start", Type = "start" }] };
        Assert.Equal(AssistantErrorKind.Input, (await Assert.ThrowsAsync<AssistantException>(() =>
            Assistant().ExplainAsync("sk-ant-test", empty, TestContext.Current.CancellationToken))).Kind);
    }

    [Fact]
    public async Task Overloaded_service_is_asked_again_but_only_a_few_times()
    {
        var ct = TestContext.Current.CancellationToken;
        _api.ReplyError(529, "overloaded_error", "Overloaded");
        _api.ReplyDraft(ValidDraft());
        var draft = await Assistant(maxRetries: 1).DraftAsync("sk-ant-test", Ask(), ct);
        Assert.Equal(2, _api.Requests.Count);
        Assert.NotEmpty(draft.Nodes);

        _api.ReplyError(529, "overloaded_error", "Overloaded");
        _api.ReplyError(529, "overloaded_error", "Overloaded");
        await Assert.ThrowsAsync<AssistantException>(() => Assistant(maxRetries: 1).DraftAsync("sk-ant-test", Ask(), ct));
        Assert.Equal(4, _api.Requests.Count);

        // Ошибка шлюза: за ним запрос мог выполниться (и быть оплачен) — не повторяем.
        _api.ReplyError(504, "api_error", "Gateway Timeout");
        await Assert.ThrowsAsync<AssistantException>(() => Assistant(maxRetries: 2).DraftAsync("sk-ant-test", Ask(), ct));
        Assert.Equal(5, _api.Requests.Count);
    }

    [Fact]
    public async Task Refusal_and_service_errors_have_clear_reasons()
    {
        var assistant = Assistant();
        var ct = TestContext.Current.CancellationToken;

        _api.ReplyRefusal();
        var refusal = await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-test", Ask(), ct));
        Assert.Equal(AssistantErrorKind.Answer, refusal.Kind);
        Assert.Contains("отказалась", refusal.Message);

        _api.ReplyDraft(ValidDraft(), stopReason: "max_tokens");
        Assert.Contains("слишком большим", (await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-test", Ask(), ct))).Message);

        _api.ReplyError(401, "authentication_error", "invalid x-api-key");
        var key = await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-wrong", Ask(), ct));
        Assert.Equal(AssistantErrorKind.Key, key.Kind);
        Assert.Contains("не подошёл", key.Message);

        _api.ReplyError(429, "rate_limit_error", "slow down");
        Assert.Equal(AssistantErrorKind.Unavailable,
            (await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-test", Ask(), ct))).Kind);

        _api.ReplyError(529, "overloaded_error", "Overloaded");
        Assert.Contains("перегружен", (await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-test", Ask(), ct))).Message);

        _api.ReplyError(400, "invalid_request_error", "Your credit balance is too low");
        Assert.Contains("credit balance", (await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-test", Ask(), ct))).Message);

        // Без ключа и без описания — запрос не отправляется вовсе.
        var requests = _api.Requests.Count;
        Assert.Equal(AssistantErrorKind.Key, (await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync(" ", Ask(), ct))).Kind);
        Assert.Equal(AssistantErrorKind.Input,
            (await Assert.ThrowsAsync<AssistantException>(() => assistant.DraftAsync("sk-ant-test", Ask("  "), ct))).Kind);
        Assert.Equal(requests, _api.Requests.Count);
    }

    [Fact]
    public async Task Key_check_reads_the_model_without_spending_tokens()
    {
        _api.ReplyModel();
        Assert.Equal("Claude Opus 5.5", await Assistant().CheckKeyAsync("sk-ant-test", TestContext.Current.CancellationToken));
        Assert.Equal("/v1/models/claude-opus-5-5", _api.Requests.Single().Path);
    }

    [Fact]
    public void Examples_given_to_the_model_pass_the_same_check()
    {
        var registry = Registry();
        var converter = new DraftConverter(registry, new ScenarioValidator(registry, new JintExpressionEvaluator()));
        var converted = converter.Convert(ScenarioPrompt.ToDraft(Example()), []);
        Assert.Empty(converted.Problems);
        Assert.Equal("log", converted.EntryNodeId);
    }
}
