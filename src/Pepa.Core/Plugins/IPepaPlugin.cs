using Pepa.Core.Nodes;

namespace Pepa.Core.Plugins;

/// <summary>
/// Расширение Pepa: свои блоки для конструктора. Сборка с классом, который реализует этот интерфейс (и имеет конструктор
/// без параметров), кладётся в папку данных Pepa: plugins/&lt;имя расширения&gt;/. При запуске Pepa вызывает
/// <see cref="Register"/> один раз.
/// </summary>
/// <remarks>
/// Блок расширения подчиняется тем же правилам, что встроенные: <see cref="NodeTypeDescriptor.Effect"/> честно говорит,
/// меняет ли блок что-то снаружи, а блок с внешним действием либо умеет проверить результат
/// (<see cref="INodeExecutor.VerifyAsync"/>), либо после сбоя оставляет решение пользователю. Код расширения работает с
/// правами Pepa — ставьте только расширения, которым доверяете.
/// </remarks>
public interface IPepaPlugin
{
    /// <summary>Название для «Настроек» → «Расширения».</summary>
    string Name { get; }

    void Register(PluginRegistration registration);
}

/// <summary>Что расширение добавляет в Pepa и что ему доступно.</summary>
/// <param name="resolveInWorkspace">Путь внутри рабочей папки с теми же проверками, что у встроенных блоков файлов.</param>
public sealed class PluginRegistration(string id, string dataDirectory, Func<string> workspaceRoot, Func<string, string>? resolveInWorkspace = null)
{
    private readonly List<NodeCategory> _categories = [];
    private readonly List<INodeExecutor> _nodes = [];

    /// <summary>Имя папки расширения.</summary>
    public string Id { get; } = id;

    /// <summary>Своя папка для данных расширения (создаётся при первом обращении).</summary>
    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(dataDirectory);
            return dataDirectory;
        }
    }

    /// <summary>Рабочая папка Pepa (из «Настроек»; может смениться, поэтому — функция).</summary>
    public Func<string> WorkspaceRoot { get; } = workspaceRoot;

    /// <summary>
    /// Полный путь к файлу внутри рабочей папки — с теми же проверками, что у встроенных блоков файлов (не выйти за папку,
    /// в том числе через ссылки на другие папки; без запрещённых имён). Нельзя — <see cref="NodeInputException"/> с причиной.
    /// </summary>
    public string ResolveWorkspacePath(string path) =>
        resolveInWorkspace is { } resolve ? resolve(path) : throw new NodeInputException("Рабочая папка Pepa недоступна расширениям.");

    public IReadOnlyList<NodeCategory> Categories => _categories;
    public IReadOnlyList<INodeExecutor> Nodes => _nodes;

    /// <summary>Свой раздел палитры конструктора.</summary>
    public PluginRegistration AddCategory(NodeCategory category)
    {
        _categories.Add(category);
        return this;
    }

    /// <summary>Блок. Тип — с точкой и своим префиксом (например, «mycompany.report»), чтобы не совпасть с чужими.</summary>
    public PluginRegistration AddNode(INodeExecutor node)
    {
        _nodes.Add(node);
        return this;
    }
}
