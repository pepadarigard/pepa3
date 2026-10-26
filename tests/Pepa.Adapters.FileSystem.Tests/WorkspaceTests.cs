using Pepa.Core.Nodes;

namespace Pepa.Adapters.FileSystem.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pepa-ws-tests", Guid.NewGuid().ToString("N"), "work");
    private readonly Workspace _workspace;

    public WorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = new Workspace(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("отчёт.txt")]
    [InlineData("папка/вложенная/файл.txt")]
    [InlineData("папка\\файл.txt")]
    [InlineData("./a/../b.txt")]
    public void Resolves_paths_inside_root(string path)
    {
        var full = _workspace.Resolve(path);
        Assert.StartsWith(_workspace.Root + Path.DirectorySeparatorChar, full);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("..")]
    public void Rejects_escape_from_root(string path)
    {
        var error = Assert.Throws<NodeInputException>(() => _workspace.Resolve(path));
        Assert.Contains("за пределы рабочей папки", error.Message);
    }

    [Fact]
    public void Absolute_paths_are_allowed_only_inside_root()
    {
        var inside = Path.Combine(_root, "a.txt");
        Assert.Equal(inside, _workspace.Resolve(inside));

        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.txt");
        Assert.Throws<NodeInputException>(() => _workspace.Resolve(outside));

        // Соседняя папка с тем же префиксом имени — тоже снаружи.
        Assert.Throws<NodeInputException>(() => _workspace.Resolve(_root + "-evil" + Path.DirectorySeparatorChar + "x.txt"));
    }

    [Theory]
    [InlineData("a?.txt")]
    [InlineData("a*.txt")]
    [InlineData("a|b.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("CON")]
    [InlineData("папка/nul.txt")]
    [InlineData("точка.")]
    public void Rejects_names_unsafe_on_windows(string path)
    {
        Assert.Throws<NodeInputException>(() => _workspace.Resolve(path));
    }

    [Fact]
    public void Root_itself_requires_explicit_permission()
    {
        Assert.Throws<NodeInputException>(() => _workspace.Resolve("."));
        Assert.Throws<NodeInputException>(() => _workspace.Resolve(""));
        Assert.Equal(_workspace.Root, _workspace.Resolve("", allowRoot: true));
    }

    [Fact]
    public void Rejects_paths_through_symbolic_links()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Создание символических ссылок недоступно в этом окружении.");
        }

        var error = Assert.Throws<NodeInputException>(() => _workspace.Resolve("link/secret.txt"));
        Assert.Contains("символическую ссылку", error.Message);
    }

    [Fact]
    public void Relative_paths_use_forward_slashes()
    {
        var full = _workspace.Resolve("a\\b\\c.txt");
        Assert.Equal("a/b/c.txt", _workspace.ToRelative(full));
    }
}
