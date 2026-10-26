namespace Pepa.Core.Model;

/// <summary>Индексы по сценарию для быстрого поиска блоков и связей.</summary>
public sealed class ScenarioGraph
{
    private readonly Dictionary<string, NodeDefinition> _nodes;
    private readonly Dictionary<(string Node, string Port), List<EdgeDefinition>> _outgoing;
    private readonly Dictionary<string, List<EdgeDefinition>> _incoming;

    public ScenarioGraph(ScenarioDefinition definition)
    {
        Definition = definition;
        _nodes = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            _nodes.TryAdd(node.Id, node);
        }

        _outgoing = new Dictionary<(string, string), List<EdgeDefinition>>();
        _incoming = new Dictionary<string, List<EdgeDefinition>>(StringComparer.Ordinal);
        foreach (var edge in definition.Edges)
        {
            var key = (edge.Source, edge.SourcePort);
            if (!_outgoing.TryGetValue(key, out var list))
            {
                _outgoing[key] = list = [];
            }

            list.Add(edge);

            if (!_incoming.TryGetValue(edge.Target, out var incoming))
            {
                _incoming[edge.Target] = incoming = [];
            }

            incoming.Add(edge);
        }
    }

    public ScenarioDefinition Definition { get; }

    public NodeDefinition? FindNode(string id) => _nodes.GetValueOrDefault(id);

    public IReadOnlyList<EdgeDefinition> Outgoing(string nodeId, string port) =>
        _outgoing.TryGetValue((nodeId, port), out var list) ? list : [];

    public IEnumerable<EdgeDefinition> OutgoingAll(string nodeId) =>
        Definition.Edges.Where(e => e.Source == nodeId);

    public IReadOnlyList<EdgeDefinition> Incoming(string nodeId) =>
        _incoming.TryGetValue(nodeId, out var list) ? list : [];

    /// <summary>
    /// Блоки «тела цикла»: достижимые из порта <paramref name="port"/> блока <paramref name="nodeId"/> и ведущие обратно в него.
    /// </summary>
    public HashSet<string> LoopBody(string nodeId, string port)
    {
        var forward = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var edge in Outgoing(nodeId, port))
        {
            if (edge.Target != nodeId && forward.Add(edge.Target))
            {
                queue.Enqueue(edge.Target);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var edge in OutgoingAll(queue.Dequeue()))
            {
                if (edge.Target != nodeId && forward.Add(edge.Target))
                {
                    queue.Enqueue(edge.Target);
                }
            }
        }

        var backward = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(nodeId);
        while (queue.Count > 0)
        {
            foreach (var edge in Incoming(queue.Dequeue()))
            {
                if (edge.Source != nodeId && forward.Contains(edge.Source) && backward.Add(edge.Source))
                {
                    queue.Enqueue(edge.Source);
                }
            }
        }

        return backward;
    }

    /// <summary>Единственная связь из порта или null, если порт не подключён.</summary>
    public EdgeDefinition? Next(string nodeId, string port)
    {
        var edges = Outgoing(nodeId, port);
        return edges.Count == 1 ? edges[0] : null;
    }
}
