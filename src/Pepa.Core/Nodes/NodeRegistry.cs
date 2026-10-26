namespace Pepa.Core.Nodes;

/// <summary>
/// Реестр типов блоков. Точка расширения: встроенные блоки, адаптеры интеграций и в будущем
/// пользовательские расширения регистрируют здесь своих исполнителей.
/// </summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<string, INodeExecutor> _executors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeCategory> _categories = new(StringComparer.Ordinal);

    public NodeRegistry Register(INodeExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var type = executor.Descriptor.Type;
        if (!_executors.TryAdd(type, executor))
        {
            throw new InvalidOperationException($"Тип блока «{type}» уже зарегистрирован.");
        }

        return this;
    }

    public NodeRegistry RegisterCategory(NodeCategory category)
    {
        _categories[category.Id] = category;
        return this;
    }

    public bool TryGet(string type, out INodeExecutor executor) =>
        _executors.TryGetValue(type, out executor!);

    public NodeTypeDescriptor? FindDescriptor(string type) =>
        _executors.TryGetValue(type, out var executor) ? executor.Descriptor : null;

    public IReadOnlyList<NodeTypeDescriptor> Descriptors =>
        _executors.Values.Select(e => e.Descriptor).ToList();

    public IReadOnlyList<NodeCategory> Categories =>
        _categories.Values.OrderBy(c => c.Order).ToList();
}
