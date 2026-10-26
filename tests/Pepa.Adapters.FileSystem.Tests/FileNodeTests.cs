using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;

namespace Pepa.Adapters.FileSystem.Tests;

/// <summary>Поведение каждого файлового действия и его проверки результата.</summary>
public sealed class FileNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pepa-file-tests", Guid.NewGuid().ToString("N"));
    private readonly Workspace _workspace;

    public FileNodeTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = new Workspace(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string P(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static NodeExecutionContext Context(object input, JsonObject? checkpoint = null) => new()
    {
        ProcessId = "p",
        Node = new NodeDefinition { Id = "n", Type = "test" },
        Input = (JsonObject)JsonSerializer.SerializeToNode(input, PepaJson.Options)!,
        Checkpoint = checkpoint,
        Variables = new JsonObject(),
        Attempt = 1,
        Time = TimeProvider.System,
    };

    private static async Task<(NodeResult Result, NodeExecutionContext Context)> Run(INodeExecutor node, object input)
    {
        var context = Context(input);
        var checkpoint = await node.PrepareAsync(context, CancellationToken.None);
        context = context with { Checkpoint = checkpoint };
        return (await node.ExecuteAsync(context, CancellationToken.None), context);
    }

    // ───────────── Запись ─────────────

    [Fact]
    public async Task Write_creates_folders_and_file()
    {
        var node = new WriteFileNode(_workspace);
        var (result, _) = await Run(node, new { path = "отчёты/2026/итог.txt", content = "Привет", mode = "overwrite" });

        var ok = Assert.IsType<NodeResult.Success>(result);
        Assert.Equal("отчёты/2026/итог.txt", ok.Output!.GetString("path"));
        Assert.Equal("Привет", await File.ReadAllTextAsync(P("отчёты/2026/итог.txt")));
    }

    [Fact]
    public async Task Write_overwrite_verification_reflects_actual_content()
    {
        var node = new WriteFileNode(_workspace);
        var input = new { path = "a.txt", content = "новое", mode = "overwrite" };
        await File.WriteAllTextAsync(P("a.txt"), "старое");
        var context = Context(input) with { Checkpoint = await node.PrepareAsync(Context(input), CancellationToken.None) };

        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        await node.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Write_create_new_never_touches_existing_file()
    {
        var node = new WriteFileNode(_workspace);
        await File.WriteAllTextAsync(P("a.txt"), "чужое");

        var (result, _) = await Run(node, new { path = "a.txt", content = "моё", mode = "createNew" });

        var failure = Assert.IsType<NodeResult.Failure>(result);
        Assert.False(failure.Retryable);
        Assert.Equal("чужое", await File.ReadAllTextAsync(P("a.txt")));
    }

    [Fact]
    public async Task Append_verification_distinguishes_once_none_and_twice()
    {
        var node = new WriteFileNode(_workspace);
        await File.WriteAllTextAsync(P("log.txt"), "строка 1\n");
        var input = new { path = "log.txt", content = "строка 2\n", mode = "append" };
        var context = Context(input) with { Checkpoint = await node.PrepareAsync(Context(input), CancellationToken.None) };

        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);

        await node.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);

        await node.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(VerificationOutcome.Inconclusive, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Write_rejects_path_outside_workspace_before_any_change()
    {
        var node = new WriteFileNode(_workspace);
        await Assert.ThrowsAsync<NodeInputException>(async () =>
            await node.PrepareAsync(Context(new { path = "../escape.txt", content = "x" }), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.txt")));
    }

    [Fact]
    public async Task Overwrite_leaves_no_temporary_files()
    {
        var node = new WriteFileNode(_workspace);
        await Run(node, new { path = "a.txt", content = "1" });
        await Run(node, new { path = "a.txt", content = "2" });

        Assert.Equal(["a.txt"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    // ───────────── Чтение, наличие, список ─────────────

    [Fact]
    public async Task Read_saves_text_to_variable()
    {
        await File.WriteAllTextAsync(P("in.txt"), "содержимое", Encoding.UTF8);
        var (result, _) = await Run(new ReadFileNode(_workspace), new { path = "in.txt", saveTo = "text" });

        var ok = Assert.IsType<NodeResult.Success>(result);
        Assert.Equal("содержимое", ok.VariableUpdates["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_of_missing_file_is_a_retryable_failure()
    {
        var (result, _) = await Run(new ReadFileNode(_workspace), new { path = "нет.txt", saveTo = "text" });
        var failure = Assert.IsType<NodeResult.Failure>(result);
        Assert.True(failure.Retryable);
        Assert.Contains("не найден", failure.Error);
    }

    [Fact]
    public async Task Exists_routes_by_presence()
    {
        Directory.CreateDirectory(P("папка"));
        await File.WriteAllTextAsync(P("f.txt"), "");
        var node = new PathExistsNode(_workspace);

        Assert.Equal(Ports.True, ((NodeResult.Success)(await Run(node, new { path = "f.txt", kind = "any" })).Result).Port);
        Assert.Equal(Ports.False, ((NodeResult.Success)(await Run(node, new { path = "папка", kind = "file" })).Result).Port);
        Assert.Equal(Ports.True, ((NodeResult.Success)(await Run(node, new { path = "папка", kind = "folder" })).Result).Port);
        var saved = (NodeResult.Success)(await Run(node, new { path = "нет.txt", kind = "any", saveTo = "found" })).Result;
        Assert.Equal(Ports.False, saved.Port);
        Assert.False(saved.VariableUpdates["found"]!.GetValue<bool>());
    }

    [Fact]
    public async Task List_returns_sorted_relative_paths()
    {
        Directory.CreateDirectory(P("in/sub"));
        await File.WriteAllTextAsync(P("in/b.txt"), "");
        await File.WriteAllTextAsync(P("in/a.txt"), "");
        await File.WriteAllTextAsync(P("in/c.log"), "");
        await File.WriteAllTextAsync(P("in/sub/d.txt"), "");
        await File.WriteAllTextAsync(P("in/.pepa-tmp-x.tmp"), "");
        var node = new ListFilesNode(_workspace);

        var flat = (NodeResult.Success)(await Run(node, new { folder = "in", pattern = "*.txt", recursive = false, saveTo = "files" })).Result;
        Assert.Equal("[\"in/a.txt\",\"in/b.txt\"]", flat.VariableUpdates["files"]!.ToJsonString());

        var deep = (NodeResult.Success)(await Run(node, new { folder = "in", pattern = "*.txt", recursive = true, saveTo = "files" })).Result;
        Assert.Equal(3, deep.VariableUpdates["files"]!.AsArray().Count);

        var all = (NodeResult.Success)(await Run(node, new { folder = "", pattern = "*", recursive = true, saveTo = "files" })).Result;
        Assert.Equal(4, all.VariableUpdates["files"]!.AsArray().Count);
    }

    // ───────────── Копирование, перемещение, удаление, папки ─────────────

    [Fact]
    public async Task Copy_verification_and_overwrite_protection()
    {
        await File.WriteAllTextAsync(P("src.txt"), "данные");
        var node = new CopyFileNode(_workspace);
        var input = new { source = "src.txt", destination = "копии/dst.txt", overwrite = false };
        var context = Context(input) with { Checkpoint = await node.PrepareAsync(Context(input), CancellationToken.None) };

        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        Assert.IsType<NodeResult.Success>(await node.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        Assert.Equal("данные", await File.ReadAllTextAsync(P("копии/dst.txt")));

        var again = await node.ExecuteAsync(context, CancellationToken.None);
        Assert.False(Assert.IsType<NodeResult.Failure>(again).Retryable);
    }

    [Fact]
    public async Task Move_verification_follows_file()
    {
        await File.WriteAllTextAsync(P("src.txt"), "данные");
        var node = new MoveFileNode(_workspace);
        var input = new { source = "src.txt", destination = "готово/src.txt", overwrite = false };
        var context = Context(input) with { Checkpoint = await node.PrepareAsync(Context(input), CancellationToken.None) };

        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        Assert.IsType<NodeResult.Success>(await node.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        Assert.False(File.Exists(P("src.txt")));
    }

    [Fact]
    public async Task Delete_respects_missing_ok_and_verifies()
    {
        var node = new DeleteFileNode(_workspace);
        Assert.IsType<NodeResult.Failure>((await Run(node, new { path = "нет.txt", missingOk = false })).Result);
        Assert.IsType<NodeResult.Success>((await Run(node, new { path = "нет.txt", missingOk = true })).Result);

        await File.WriteAllTextAsync(P("x.txt"), "x");
        var input = new { path = "x.txt", missingOk = false };
        var context = Context(input) with { Checkpoint = await node.PrepareAsync(Context(input), CancellationToken.None) };
        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        await node.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Delete_refuses_folders()
    {
        Directory.CreateDirectory(P("папка"));
        var (result, _) = await Run(new DeleteFileNode(_workspace), new { path = "папка" });
        Assert.IsType<NodeResult.Failure>(result);
        Assert.True(Directory.Exists(P("папка")));
    }

    [Fact]
    public async Task Create_folder_is_idempotent()
    {
        var node = new CreateFolderNode(_workspace);
        Assert.IsType<NodeResult.Success>((await Run(node, new { path = "a/b/c" })).Result);
        Assert.IsType<NodeResult.Success>((await Run(node, new { path = "a/b/c" })).Result);
        Assert.True(Directory.Exists(P("a/b/c")));
    }
}
