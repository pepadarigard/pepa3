using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.FileSystem.Tests;

/// <summary>Файловые действия внутри настоящего движка: сквозные сценарии и восстановление после сбоя.</summary>
public sealed class FileScenarioTests
{
    private static async Task<(EngineHarness Harness, string Root)> CreateAsync(bool withFaults = false)
    {
        string? root = null;
        var harness = await EngineHarness.CreateAsync(withFaults: withFaults, extraNodes: registry =>
        {
            return registry.RegisterFileNodes(new Workspace(() => root!));
        });
        root = Path.Combine(harness.Directory, "workspace");
        return (harness, root);
    }

    [Fact]
    public async Task Write_read_and_branch_on_content()
    {
        var (h, root) = await CreateAsync();
        await using var _ = h;
        var scenario = new ScenarioBuilder()
            .Variable("text", VariableType.Text)
            .Variable("name", VariableType.Text, "Пепа")
            .Node("start", "start")
            .Node("write", "file.write", new { path = "приветы/{{ vars.name }}.txt", content = "Привет, {{ vars.name }}!" })
            .Node("read", "file.read", new { path = "приветы/{{ vars.name }}.txt", saveTo = "text" })
            .Node("check", "condition", new { expression = "vars.text.includes('Пепа')" })
            .Node("ok", "end")
            .Node("bad", "end", new { outcome = "failure" })
            .Chain("start", "write", "read", "check")
            .Edge("check", "ok", Ports.True)
            .Edge("check", "bad", Ports.False)
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("Привет, Пепа!", process.Variables.GetString("text"));
        Assert.Equal("Привет, Пепа!", await File.ReadAllTextAsync(Path.Combine(root, "приветы", "Пепа.txt")));
    }

    [Fact]
    public async Task Path_escape_stops_process_without_touching_files()
    {
        var (h, root) = await CreateAsync();
        await using var _ = h;
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("write", "file.write", new { path = "../../вне.txt", content = "x" })
            .Node("end", "end")
            .Chain("start", "write", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("за пределы рабочей папки", process.StatusMessage);
        Assert.False(File.Exists(Path.Combine(h.Directory, "..", "вне.txt")));
    }

    [Fact]
    public async Task Missing_file_goes_to_error_branch()
    {
        var (h, _) = await CreateAsync();
        await using var __ = h;
        var scenario = new ScenarioBuilder()
            .Variable("text", VariableType.Text)
            .Variable("problem", VariableType.Text)
            .Node("start", "start")
            .Node("read", "file.read", new { path = "нет.txt", saveTo = "text" })
            .Node("note", "variable.set", new { variable = "problem", value = "lastError.message" })
            .Node("end", "end")
            .Node("unexpected", "end", new { outcome = "failure" })
            .Edge("start", "read")
            .Edge("read", "unexpected")
            .Edge("read", "note", Ports.Error)
            .Edge("note", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Contains("не найден", process.Variables.GetString("problem"));
    }

    [Fact]
    public async Task Crash_after_append_does_not_append_twice()
    {
        var (h, root) = await CreateAsync(withFaults: true);
        await using var _ = h;
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "журнал.txt");
        await File.WriteAllTextAsync(log, "начало\n");
        h.Faults.FailWhenStep = s => s is { NodeType: "file.write", State: StepState.Succeeded };
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("append", "file.write", new { path = "журнал.txt", content = "запись\n", mode = "append" })
            .Node("end", "end")
            .Chain("start", "append", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        await h.WaitForAsync(id, _ => h.Faults.Tripped);
        await Task.Delay(200);
        await h.CrashAndRestartAsync();

        Assert.Equal(AttentionKind.UnknownStep, (await h.ProcessAsync(id)).Attention);
        await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("начало\nзапись\n", await File.ReadAllTextAsync(log));
    }

    [Fact]
    public async Task Crash_before_write_reached_disk_writes_once_after_verification()
    {
        var (h, root) = await CreateAsync(withFaults: true);
        await using var _ = h;
        h.Faults.FailWhenStep = s => s is { NodeType: "file.write", State: StepState.Succeeded };
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("write", "file.write", new { path = "итог.txt", content = "готово" })
            .Node("end", "end")
            .Chain("start", "write", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        await h.WaitForAsync(id, _ => h.Faults.Tripped);
        await Task.Delay(200);
        // Имитируем, что запись до диска не дошла.
        File.Delete(Path.Combine(root, "итог.txt"));
        await h.CrashAndRestartAsync();

        var result = await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        Assert.Contains("не выполнен", result.Message);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("готово", await File.ReadAllTextAsync(Path.Combine(root, "итог.txt")));
    }
}
