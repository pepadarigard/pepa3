using System.Text.Json.Nodes;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;

namespace Pepa.Core.Tests;

public sealed class ValidatorTests
{
    private readonly ScenarioValidator _validator;

    public ValidatorTests()
    {
        var registry = new NodeRegistry()
            .Register(new FakeNode(new NodeTypeDescriptor
            {
                Type = "start", Category = "logic", Title = "Начало", Description = "", IsStart = true,
                Outputs = [new PortDescriptor(Ports.Out, "Далее")],
            }))
            .Register(new FakeNode(new NodeTypeDescriptor
            {
                Type = "end", Category = "logic", Title = "Завершение", Description = "", IsEnd = true,
            }))
            .Register(new FakeNode(new NodeTypeDescriptor
            {
                Type = "condition", Category = "logic", Title = "Условие", Description = "",
                Outputs = [new PortDescriptor(Ports.True, "Да"), new PortDescriptor(Ports.False, "Нет")],
                Fields = [new FieldDescriptor { Name = "expression", Label = "Условие", Kind = FieldKind.Condition, Required = true }],
            }))
            .Register(new FakeNode(new NodeTypeDescriptor
            {
                Type = "act", Category = "test", Title = "Действие", Description = "", Effect = NodeEffect.External,
                Outputs = [new PortDescriptor(Ports.Out, "Далее"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
                Fields =
                [
                    new FieldDescriptor { Name = "path", Label = "Путь", Kind = FieldKind.Path, Required = true },
                    new FieldDescriptor { Name = "saveTo", Label = "Сохранить в", Kind = FieldKind.Variable },
                    new FieldDescriptor { Name = "count", Label = "Количество", Kind = FieldKind.Number, Min = 1, Max = 10 },
                    new FieldDescriptor
                    {
                        Name = "mode", Label = "Режим", Kind = FieldKind.Select, Default = "a",
                        Options = [new("a", "А"), new("b", "Б")],
                    },
                    new FieldDescriptor
                    {
                        Name = "time", Label = "Время", Kind = FieldKind.Time, Required = true,
                        VisibleWhen = new FieldCondition("mode", ["b"]),
                    },
                ],
            }));
        _validator = new ScenarioValidator(registry, new JintExpressionEvaluator());
    }

    private static NodeDefinition Node(string id, string type, JsonObject? config = null) =>
        new() { Id = id, Type = type, Config = config ?? new JsonObject() };

    private static EdgeDefinition Edge(string id, string source, string target, string port = Ports.Out) =>
        new() { Id = id, Source = source, Target = target, SourcePort = port };

    private static ScenarioDefinition Valid() => new()
    {
        Name = "Проверка",
        Nodes = [Node("s", "start"), Node("a", "act", new JsonObject { ["path"] = "out.txt" }), Node("e", "end")],
        Edges = [Edge("e1", "s", "a"), Edge("e2", "a", "e")],
    };

    private IReadOnlyList<string> Codes(ScenarioDefinition scenario, IssueSeverity? severity = null) =>
        _validator.Validate(scenario).Issues
            .Where(i => severity is null || i.Severity == severity)
            .Select(i => i.Code).ToList();

    [Fact]
    public void Minimal_scenario_is_valid()
    {
        var report = _validator.Validate(Valid());
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Message)));
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Requires_name_start_and_end()
    {
        var scenario = new ScenarioDefinition { Name = " ", Nodes = [Node("a", "act", new JsonObject { ["path"] = "x" })] };
        var codes = Codes(scenario, IssueSeverity.Error);
        Assert.Contains("scenario.name", codes);
        Assert.Contains("start.missing", codes);
        Assert.Contains("end.missing", codes);
    }

    [Fact]
    public void Only_one_start_allowed()
    {
        var scenario = Valid() with { Nodes = [.. Valid().Nodes, Node("s2", "start")], Edges = [.. Valid().Edges, Edge("e3", "s2", "e")] };
        Assert.Contains("start.multiple", Codes(scenario));
    }

    [Fact]
    public void Unknown_type_and_duplicate_ids_are_errors()
    {
        var scenario = Valid() with { Nodes = [.. Valid().Nodes, Node("x", "nope"), Node("a", "end")] };
        var codes = Codes(scenario);
        Assert.Contains("node.type.unknown", codes);
        Assert.Contains("node.id.duplicate", codes);
    }

    [Fact]
    public void Required_ports_must_be_connected_error_port_is_optional()
    {
        var scenario = Valid() with { Edges = [Edge("e1", "s", "a")] };
        var issues = _validator.Validate(scenario).Issues;
        var unconnected = Assert.Single(issues, i => i.Code == "port.unconnected");
        Assert.Equal("a", unconnected.NodeId);
        Assert.Contains("«Далее»", unconnected.Message);
    }

    [Fact]
    public void Several_edges_from_one_port_are_rejected()
    {
        var scenario = Valid() with
        {
            Nodes = [.. Valid().Nodes, Node("e2", "end")],
            Edges = [.. Valid().Edges, Edge("e3", "a", "e2")],
        };
        Assert.Contains("port.multiple", Codes(scenario));
    }

    [Fact]
    public void Edges_must_reference_existing_nodes_and_ports_and_not_enter_start()
    {
        var scenario = Valid() with
        {
            Edges = [.. Valid().Edges, Edge("e3", "a", "ghost"), Edge("e4", "a", "e", "nope"), Edge("e5", "a", "s", Ports.Error)],
        };
        var codes = Codes(scenario);
        Assert.Contains("edge.dangling", codes);
        Assert.Contains("edge.port.unknown", codes);
        Assert.Contains("edge.into.start", codes);
    }

    [Fact]
    public void Field_values_are_checked()
    {
        var scenario = Valid() with
        {
            Nodes =
            [
                Node("s", "start"),
                Node("a", "act", new JsonObject { ["path"] = "{{ vars.x + }}", ["count"] = 50, ["mode"] = "c", ["saveTo"] = "ghost" }),
                Node("e", "end"),
            ],
        };
        var codes = Codes(scenario, IssueSeverity.Error);
        Assert.Contains("field.template", codes);
        Assert.Contains("field.range", codes);
        Assert.Contains("field.select", codes);
        Assert.Contains("field.variable.undeclared", codes);
    }

    [Fact]
    public void Hidden_fields_are_not_required()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);

        var visible = Valid() with
        {
            Nodes = [Node("s", "start"), Node("a", "act", new JsonObject { ["path"] = "x", ["mode"] = "b" }), Node("e", "end")],
        };
        Assert.Contains("field.required", Codes(visible));

        var badTime = Valid() with
        {
            Nodes = [Node("s", "start"), Node("a", "act", new JsonObject { ["path"] = "x", ["mode"] = "b", ["time"] = "25:00" }), Node("e", "end")],
        };
        Assert.Contains("field.time", Codes(badTime));
    }

    [Fact]
    public void Condition_expression_syntax_is_checked()
    {
        var scenario = new ScenarioDefinition
        {
            Name = "Условие",
            Nodes = [Node("s", "start"), Node("c", "condition", new JsonObject { ["expression"] = "vars.a >" }), Node("e", "end")],
            Edges = [Edge("e1", "s", "c"), Edge("e2", "c", "e", Ports.True), Edge("e3", "c", "e", Ports.False)],
        };
        Assert.Contains("field.expression", Codes(scenario));
    }

    [Fact]
    public void Unreachable_and_endless_nodes_are_warnings()
    {
        var scenario = Valid() with
        {
            Nodes = [.. Valid().Nodes, Node("lonely", "act", new JsonObject { ["path"] = "x" }), Node("loop", "condition", new JsonObject { ["expression"] = "true" })],
            Edges =
            [
                Edge("e1", "s", "loop"), Edge("e2", "loop", "loop", Ports.True), Edge("e3", "loop", "loop", Ports.False),
                Edge("e4", "a", "e"), Edge("e5", "lonely", "e"),
            ],
        };
        var report = _validator.Validate(scenario);
        Assert.True(report.IsValid);
        Assert.Contains(report.Issues, i => i is { Code: "node.unreachable", NodeId: "lonely" });
        Assert.Contains(report.Issues, i => i is { Code: "node.no.exit", NodeId: "loop" });
    }

    [Fact]
    public void Variable_names_are_checked()
    {
        var scenario = Valid() with
        {
            Variables =
            [
                new VariableDefinition { Name = "счётчик" },
                new VariableDefinition { Name = "1abc" },
                new VariableDefinition { Name = "vars" },
                new VariableDefinition { Name = "счётчик" },
                new VariableDefinition { Name = "n", Type = VariableType.Number, Default = "текст" },
            ],
        };
        var issues = _validator.Validate(scenario).Issues;
        Assert.Equal(2, issues.Count(i => i.Code == "variable.name"));
        Assert.Single(issues, i => i.Code == "variable.duplicate");
        Assert.Single(issues, i => i.Code == "variable.default.type");
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        Assert.Contains("schema.version", Codes(Valid() with { SchemaVersion = 99 }));
    }

    private sealed class FakeNode(NodeTypeDescriptor descriptor) : INodeExecutor
    {
        public NodeTypeDescriptor Descriptor { get; } = descriptor;

        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NodeResult>(NodeResult.Ok());
    }
}
