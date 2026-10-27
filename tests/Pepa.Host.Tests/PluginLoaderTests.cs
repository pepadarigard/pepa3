using System.Text;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Plugins;
using Pepa.Engine.Nodes;
using Pepa.Host.Plugins;

namespace Pepa.Host.Tests;

/// <summary>Проверка расширений при подключении: ошибка расширения — отказ с причиной, а не сбой Pepa или подмена палитры.</summary>
public sealed class PluginLoaderTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "sample-plugin", "Pepa.Plugins.Sample.dll");

    private sealed class TestPlugin(Action<PluginRegistration> register) : IPepaPlugin
    {
        public string Name => "Проверочное";

        public void Register(PluginRegistration registration) => register(registration);
    }

    private sealed class TestNode(Func<NodeTypeDescriptor> descriptor, Func<NodeDefinition, IEnumerable<NodeConfigIssue>>? validate = null) : INodeExecutor
    {
        public NodeTypeDescriptor Descriptor => descriptor();

        public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node) => validate?.Invoke(node) ?? [];

        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, "ок"));
    }

    private static readonly NodeCategory Own = new("test-plugin", "Проверочные", 100);

    private static NodeTypeDescriptor Valid(string type = "test-plugin.block") => new()
    {
        Type = type,
        Category = Own.Id,
        Title = "Блок",
        Description = "Проверочный блок.",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields = [new FieldDescriptor { Name = "text", Label = "Текст", Kind = FieldKind.Text }],
    };

    private static (PluginInfo Info, NodeRegistry Registry) Accept(Action<PluginRegistration> register)
    {
        var registry = new NodeRegistry().RegisterLogicNodes().RegisterFlowNodes();
        var info = PluginLoader.Accept("test", "plugins/test", Path.Combine(Path.GetTempPath(), "pepa-plugin-data"),
            [(new TestPlugin(register), "1.0")], registry, () => Path.GetTempPath(), null);
        return (info, registry);
    }

    [Fact]
    public void Plugin_cannot_replace_a_palette_section_of_Pepa_or_of_another_plugin()
    {
        var (info, registry) = Accept(r => r.AddCategory(new NodeCategory("logic", "ПОДМЕНЁННАЯ ЛОГИКА", -100))
            .AddNode(new TestNode(() => Valid() with { Category = "logic" })));
        Assert.Contains("раздел палитры «logic» уже есть", info.Error);
        Assert.NotEqual("ПОДМЕНЁННАЯ ЛОГИКА", registry.Categories.Single(c => c.Id == "logic").Title);
        Assert.Null(registry.FindDescriptor("test-plugin.block"));
    }

    public static TheoryData<string, string> Malformed => new()
    {
        { "title", "название или описание" },
        { "ports", "свой непустой id" },
        { "field", "своё непустое имя" },
        { "select", "нет вариантов" },
        { "summary", "SummaryField" },
        { "verification", "только у блоков с внешним действием" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_block_description_is_refused_with_a_reason(string what, string reason)
    {
        var descriptor = what switch
        {
            "title" => Valid() with { Title = null! },
            "ports" => Valid() with { Outputs = [new PortDescriptor(Ports.Out, "Далее"), new PortDescriptor(Ports.Out, "Ещё")] },
            "field" => Valid() with { Fields = [new FieldDescriptor { Name = null!, Label = "Поле", Kind = FieldKind.Text }] },
            "select" => Valid() with { Fields = [new FieldDescriptor { Name = "mode", Label = "Режим", Kind = FieldKind.Select }] },
            // Проверка без внешнего действия: после сбоя Pepa повторила бы блок, так и не вызвав проверку.
            "verification" => Valid() with { SupportsVerification = true, Effect = NodeEffect.ReadOnly },
            _ => Valid() with { SummaryField = "нет-такого" },
        };
        var (info, registry) = Accept(r => r.AddCategory(Own).AddNode(new TestNode(() => descriptor)));
        Assert.Contains(reason, info.Error);
        Assert.Null(registry.FindDescriptor("test-plugin.block"));
    }

    [Fact]
    public void Description_is_read_once_and_plugin_errors_do_not_escape()
    {
        // Описание, которое со второго раза бросает исключение: Pepa работает с копией, прочитанной при подключении.
        var reads = 0;
        var (info, registry) = Accept(r => r.AddCategory(Own).AddNode(new TestNode(
            () => ++reads == 1 ? Valid() : throw new InvalidOperationException("второй раз"),
            _ => throw new InvalidOperationException("проверка сломалась"))));
        Assert.Null(info.Error);
        Assert.Equal("Блок", registry.FindDescriptor("test-plugin.block")!.Title);
        Assert.Contains(registry.Descriptors, d => d.Type == "test-plugin.block");
        Assert.True(registry.TryGet("test-plugin.block", out var node));
        // Ошибка в проверке параметров расширения — замечание, а не сбой проверки сценария.
        var issue = Assert.Single(node.Validate(new NodeDefinition { Id = "n", Type = "test-plugin.block" }));
        Assert.Contains("проверка сломалась", issue.Message);

        var (thrown, _) = Accept(_ => throw new ArgumentException("не то"));
        Assert.Contains("ошибка при подключении: не то", thrown.Error);
        var (badDescriptor, _) = Accept(r => r.AddCategory(Own).AddNode(new TestNode(() => throw new InvalidOperationException("нет описания"))));
        Assert.Contains("нет описания", badDescriptor.Error);
    }

    [Fact]
    public void Plugins_are_not_loaded_when_Pepa_runs_as_administrator()
    {
        var root = Path.Combine(Path.GetTempPath(), "pepa-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sample"));
        File.Copy(Sample, Path.Combine(root, "sample", "Pepa.Plugins.Sample.dll"));
        try
        {
            var registry = new NodeRegistry().RegisterLogicNodes();
            var info = Assert.Single(PluginLoader.Load(root, root, registry, () => root, elevated: true).Plugins);
            Assert.Contains("от имени администратора", info.Error);
            Assert.Null(registry.FindDescriptor("sample.appendline"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NodeExecutionContext Context(string line, JsonObject? checkpoint = null) => new()
    {
        ProcessId = "p1",
        Node = new NodeDefinition { Id = "append", Type = "sample.appendline" },
        Input = new JsonObject { ["path"] = "журнал.txt", ["line"] = line },
        Variables = [],
        Attempt = 1,
        Time = TimeProvider.System,
        Checkpoint = checkpoint,
    };

    [Fact]
    public async Task Sample_append_block_verifies_by_bytes_and_never_says_not_done_after_a_partial_write()
    {
        var root = Path.Combine(Path.GetTempPath(), "pepa-plugin-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Своим контекстом загрузки, как в Pepa: в общем контексте сборка примера считалась бы сборкой Pepa в других проверках.
            var assembly = new System.Runtime.Loader.AssemblyLoadContext("sample-under-test").LoadFromAssemblyPath(Sample);
            var plugin = (IPepaPlugin)Activator.CreateInstance(assembly.GetType("Pepa.Plugins.Sample.SamplePlugin")!)!;
            var registry = new NodeRegistry().RegisterLogicNodes();
            var workspace = new Adapters.FileSystem.Workspace(() => root);
            Assert.Null(PluginLoader.Accept("sample", root, root, [(plugin, null)], registry, () => root, p => workspace.Resolve(p)).Error);
            Assert.True(registry.TryGet("sample.appendline", out var append));
            var ct = TestContext.Current.CancellationToken;
            var file = Path.Combine(root, "журнал.txt");

            // Новый файл: дописанная строка находится (раньше пометка BOM сдвигала проверку, и строка дописывалась второй раз).
            var checkpoint = await append.PrepareAsync(Context("Привет"), ct);
            await append.ExecuteAsync(Context("Привет", checkpoint), ct);
            Assert.Equal(VerificationOutcome.Done, (await append.VerifyAsync(Context("Привет", checkpoint), ct)).Outcome);
            Assert.False(File.ReadAllBytes(file).AsSpan().StartsWith(Encoding.UTF8.Preamble), "в новом файле не должно быть BOM");

            // Ничего не дописано (в том числе пустая строка) — «не выполнено»: дописать можно.
            checkpoint = await append.PrepareAsync(Context(""), ct);
            Assert.Equal(VerificationOutcome.NotDone, (await append.VerifyAsync(Context("", checkpoint), ct)).Outcome);

            // Дописана часть строки — «неизвестно»: повтор дописал бы её ещё раз.
            checkpoint = await append.PrepareAsync(Context("Ещё строка"), ct);
            await File.AppendAllTextAsync(file, "Ещё", ct);
            Assert.Equal(VerificationOutcome.Inconclusive, (await append.VerifyAsync(Context("Ещё строка", checkpoint), ct)).Outcome);

            // Путь — с проверками Pepa: за рабочую папку не выйти.
            await Assert.ThrowsAsync<NodeInputException>(async () =>
                await append.PrepareAsync(Context("x") with { Input = new JsonObject { ["path"] = "../чужое.txt", ["line"] = "x" } }, ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
