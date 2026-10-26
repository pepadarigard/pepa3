using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Serialization;

namespace Pepa.Core.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void Scenario_round_trips_through_json()
    {
        var scenario = new ScenarioDefinition
        {
            Id = "s1",
            Name = "Привет",
            Description = "Описание",
            Variables = [new VariableDefinition { Name = "n", Type = VariableType.Number, Default = 3, IsInput = true }],
            Nodes =
            [
                new NodeDefinition
                {
                    Id = "a", Type = "file.write", Label = "Запись", Position = new NodePosition(10, 20),
                    Config = new JsonObject { ["path"] = "a.txt" },
                    Settings = new NodeSettings { RetryCount = 2, RetryDelaySeconds = 1, TimeoutSeconds = 30 },
                },
            ],
            Edges = [new EdgeDefinition { Id = "e1", Source = "a", SourcePort = Ports.Error, Target = "b" }],
        };

        var json = PepaJson.Serialize(scenario);
        var restored = PepaJson.Deserialize<ScenarioDefinition>(json);

        Assert.Contains("\"type\":\"number\"", json);
        Assert.Contains("Привет", json);
        Assert.Equal(json, PepaJson.Serialize(restored));
        Assert.Equal(2, restored.Nodes[0].Settings.RetryCount);
        Assert.Equal(VariableType.Number, restored.Variables[0].Type);
    }

    [Fact]
    public void Missing_optional_fields_get_defaults()
    {
        var restored = PepaJson.Deserialize<ScenarioDefinition>(
            """{"name":"Мини","nodes":[{"id":"s","type":"start"}],"edges":[{"id":"e","source":"s","target":"x"}]}""");

        Assert.Equal(ScenarioDefinition.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(Ports.Out, restored.Edges[0].SourcePort);
        Assert.Empty(restored.Nodes[0].Config);
        Assert.Equal(0, restored.Nodes[0].Settings.RetryCount);
    }
}
