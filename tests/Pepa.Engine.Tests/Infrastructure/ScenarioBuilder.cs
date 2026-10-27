using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Serialization;

namespace Pepa.Engine.Tests.Infrastructure;

/// <summary>Краткая запись графа для тестов.</summary>
public sealed class ScenarioBuilder(string name = "Тестовый сценарий")
{
    private readonly List<NodeDefinition> _nodes = [];
    private readonly List<EdgeDefinition> _edges = [];
    private readonly List<VariableDefinition> _variables = [];

    public ScenarioBuilder Node(string id, string type, object? config = null, NodeSettings? settings = null, string? label = null)
    {
        _nodes.Add(new NodeDefinition
        {
            Id = id,
            Type = type,
            Label = label,
            Config = config is null ? new JsonObject() : (JsonObject)JsonSerializer.SerializeToNode(config, PepaJson.Options)!,
            Settings = settings ?? new NodeSettings(),
        });
        return this;
    }

    public ScenarioBuilder Edge(string source, string target, string port = Ports.Out)
    {
        _edges.Add(new EdgeDefinition { Id = $"e{_edges.Count + 1}", Source = source, SourcePort = port, Target = target });
        return this;
    }

    /// <summary>Цепочка блоков через выход «out».</summary>
    public ScenarioBuilder Chain(params string[] ids)
    {
        for (var i = 0; i < ids.Length - 1; i++)
        {
            Edge(ids[i], ids[i + 1]);
        }

        return this;
    }

    public ScenarioBuilder Variable(string variableName, VariableType type, JsonNode? defaultValue = null, bool isInput = false, bool secret = false)
    {
        _variables.Add(new VariableDefinition { Name = variableName, Type = type, Default = defaultValue, IsInput = isInput, Secret = secret });
        return this;
    }

    public ScenarioDefinition Build() => new()
    {
        Name = name,
        Nodes = _nodes.ToList(),
        Edges = _edges.ToList(),
        Variables = _variables.ToList(),
    };
}
