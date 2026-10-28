using System.Text.Json.Nodes;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Tables.Tests;

/// <summary>Таблицы внутри настоящего движка: перебор строк, восстановление после сбоя, параллельная запись.</summary>
public sealed class TableScenarioTests
{
    private static async Task<(EngineHarness Harness, string Root)> CreateAsync(bool withFaults = false)
    {
        string? root = null;
        var harness = await EngineHarness.CreateAsync(withFaults: withFaults,
            extraNodes: registry => registry.RegisterFileNodes(new Workspace(() => root!)).RegisterTableNodes(new Workspace(() => root!)));
        root = Path.Combine(harness.Directory, "workspace");
        Directory.CreateDirectory(root);
        return (harness, root);
    }

    [Fact]
    public async Task Every_account_from_the_table_is_marked_done_and_reported()
    {
        var (h, root) = await CreateAsync();
        await using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(root, "аккаунты.csv"), "Логин;Статус\r\nanna;новый\r\nboris;новый\r\n");
        var id = await h.RunAsync(new ScenarioBuilder("Аккаунты")
            .Variable("rows", VariableType.List)
            .Variable("row", VariableType.Any)
            .Node("start", "start")
            .Node("read", "table.read", new { path = "аккаунты.csv", saveTo = "rows" })
            .Node("each", "loop.forEach", new { items = "vars.rows", itemVariable = "row" })
            .Node("mark", "table.update", new { path = "аккаунты.csv", column = "Логин", value = "{{ vars.row.Логин }}", values = "({ Статус: 'готово' })" })
            .Node("report", "table.append", new { path = "отчёт.xlsx", values = "({ Логин: vars.row.Логин, Итог: 'ок' })" })
            .Node("end", "end")
            .Edge("start", "read").Edge("read", "each").Edge("each", "mark", Ports.Item).Edge("mark", "report").Edge("report", "each")
            .Edge("each", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("Логин;Статус\r\nanna;готово\r\nboris;готово\r\n", await File.ReadAllTextAsync(Path.Combine(root, "аккаунты.csv")));
        var findRows = new ReadTableNode(new Workspace(root));
        var report = (Pepa.Core.Nodes.NodeResult.Success)await findRows.ExecuteAsync(new Pepa.Core.Nodes.NodeExecutionContext
        {
            ProcessId = "p", Node = new NodeDefinition { Id = "n", Type = "t" },
            Input = new JsonObject { ["path"] = "отчёт.xlsx", ["saveTo"] = "rows" },
            Variables = new JsonObject(), Attempt = 1, Time = TimeProvider.System,
        }, CancellationToken.None);
        Assert.Equal(["anna", "boris"], report.VariableUpdates["rows"]!.AsArray().Select(r => r!["Логин"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Crash_after_appending_does_not_append_the_row_twice()
    {
        var (h, root) = await CreateAsync(withFaults: true);
        await using var _ = h;
        var file = Path.Combine(root, "журнал.csv");
        await File.WriteAllTextAsync(file, "Событие\r\nначало\r\n");
        h.Faults.FailWhenStep = s => s is { NodeType: AppendRowNode.TypeName, State: StepState.Succeeded };
        var id = await h.RunAsync(new ScenarioBuilder("Журнал")
            .Node("start", "start")
            .Node("append", "table.append", new { path = "журнал.csv", values = "({ Событие: 'оплата' })" })
            .Node("end", "end")
            .Chain("start", "append", "end")
            .Build());

        await h.WaitForAsync(id, _ => h.Faults.Tripped);
        await Task.Delay(200);
        await h.CrashAndRestartAsync();
        Assert.Equal(AttentionKind.UnknownStep, (await h.ProcessAsync(id)).Attention);
        await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("Событие\r\nначало\r\nоплата\r\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Parallel_processes_appending_to_one_table_do_not_lose_rows()
    {
        var (h, root) = await CreateAsync();
        await using var _ = h;
        var scenario = new ScenarioBuilder("Запись")
            .Variable("n", VariableType.Number, isInput: true)
            .Node("start", "start")
            .Node("append", "table.append", new { path = "общая.xlsx", values = "({ Номер: vars.n })" })
            .Node("end", "end")
            .Chain("start", "append", "end")
            .Build();
        var scenarioId = await h.CreateScenarioAsync(scenario);
        var ids = new List<string>();
        for (var n = 1; n <= 8; n++)
        {
            ids.Add(await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["n"] = n }));
        }

        foreach (var id in ids)
        {
            await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        }

        var node = new ReadTableNode(new Workspace(root));
        var rows = ((Pepa.Core.Nodes.NodeResult.Success)await node.ExecuteAsync(new Pepa.Core.Nodes.NodeExecutionContext
        {
            ProcessId = "p", Node = new NodeDefinition { Id = "n", Type = "t" },
            Input = new JsonObject { ["path"] = "общая.xlsx", ["saveTo"] = "rows" },
            Variables = new JsonObject(), Attempt = 1, Time = TimeProvider.System,
        }, CancellationToken.None)).VariableUpdates["rows"]!.AsArray();
        Assert.Equal(Enumerable.Range(1, 8), rows.Select(r => (int)r!["Номер"]!.GetValue<long>()).Order());
    }

    [Fact]
    public async Task Ambiguous_row_stops_the_process_with_the_reason()
    {
        var (h, root) = await CreateAsync();
        await using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(root, "a.csv"), "Логин;Статус\r\nanna;1\r\nAnna;2\r\n");
        var id = await h.RunAsync(new ScenarioBuilder("Неоднозначно")
            .Node("start", "start")
            .Node("mark", "table.update", new { path = "a.csv", column = "Логин", value = "anna", values = "({ Статус: 'x' })" })
            .Node("end", "end")
            .Chain("start", "mark", "end")
            .Build());

        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("2 строк", stopped.StatusMessage);
        Assert.Equal("Логин;Статус\r\nanna;1\r\nAnna;2\r\n", await File.ReadAllTextAsync(Path.Combine(root, "a.csv")));
    }
}
