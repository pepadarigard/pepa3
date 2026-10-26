using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Core.Validation;

/// <summary>
/// Структура параллельных участков: ветви блока «Параллельно» должны сойтись в одном блоке «Дождаться всех ветвей»,
/// внутрь участка нельзя войти иначе как через «Параллельно», ветви не сливаются раньше времени и не возвращаются к
/// разделению. Тогда движок всегда знает, сколько ветвей ждать, — и ничего не зависает навсегда.
/// </summary>
internal sealed class ParallelCheck(
    IReadOnlyDictionary<string, NodeTypeDescriptor> descriptors,
    IReadOnlyList<EdgeDefinition> edges,
    Func<string, string> name,
    Action<string, string, string?> error)
{
    private readonly Dictionary<string, List<EdgeDefinition>> _outgoing = edges.GroupBy(e => e.Source, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    private readonly Dictionary<string, List<EdgeDefinition>> _incoming = edges.GroupBy(e => e.Target, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>Разделение → (слияние, блоки участка). null — участок с ошибкой (уже сообщено).</summary>
    private readonly Dictionary<string, (string Join, HashSet<string> Region)?> _resolved = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _joinOwner = new(StringComparer.Ordinal);

    public void Run()
    {
        if (!descriptors.Values.Any(d => d.Flow != NodeFlow.Normal))
        {
            return;
        }

        foreach (var (id, descriptor) in descriptors)
        {
            if (descriptor.Flow == NodeFlow.Split)
            {
                Resolve(id, []);
            }
        }

        foreach (var (id, descriptor) in descriptors)
        {
            if (descriptor.Flow == NodeFlow.Join && !_joinOwner.ContainsKey(id))
            {
                error("parallel.join.orphan",
                    $"Блок {name(id)} ждёт ветви, но к нему не ведут ветви блока «Параллельно». Слияние ставится в конце параллельного участка.", id);
            }
        }
    }

    private (string Join, HashSet<string> Region)? Resolve(string split, HashSet<string> stack)
    {
        if (_resolved.TryGetValue(split, out var known))
        {
            return known;
        }

        if (!stack.Add(split))
        {
            error("parallel.cycle", $"Параллельный участок блока {name(split)} содержит сам себя.", split);
            return _resolved[split] = null;
        }

        var branches = _outgoing.GetValueOrDefault(split) ?? [];
        var region = new HashSet<string>(StringComparer.Ordinal);
        var branchOf = new Dictionary<string, int>(StringComparer.Ordinal);
        string? join = null;
        var ok = true;
        if (branches.Count < 2)
        {
            error("parallel.branches", $"У блока {name(split)} подключите хотя бы две ветви.", split);
            ok = false;
        }

        for (var i = 0; i < branches.Count; i++)
        {
            var port = descriptors[split].FindOutput(branches[i].SourcePort)?.Label ?? branches[i].SourcePort;
            var joins = Walk(branches[i].Target, split, i, region, branchOf, stack, ref ok);
            if (joins.Count == 0)
            {
                error("parallel.branch.open",
                    $"Ветвь «{port}» блока {name(split)} не приходит к блоку «Дождаться всех ветвей».", split);
                ok = false;
            }
            else if (joins.Count > 1 || (join is not null && joins.First() != join))
            {
                error("parallel.join.mismatch",
                    $"Ветви блока {name(split)} должны сходиться в одном блоке «Дождаться всех ветвей», а приходят в разные.", split);
                ok = false;
            }
            else
            {
                join = joins.First();
            }
        }

        if (join is not null && ok)
        {
            if (_joinOwner.TryGetValue(join, out var other) && other != split)
            {
                error("parallel.join.shared", $"В блоке {name(join)} сходятся ветви разных блоков «Параллельно» — у каждого должно быть своё слияние.", join);
                ok = false;
            }

            foreach (var edge in _incoming.GetValueOrDefault(join) ?? [])
            {
                if (!region.Contains(edge.Source))
                {
                    error("parallel.join.outside",
                        $"В блок {name(join)} входит связь не из ветвей блока {name(split)} (из {name(edge.Source)}): слияние ждёт только свои ветви.", join);
                    ok = false;
                }
            }

            foreach (var node in region)
            {
                foreach (var edge in _incoming.GetValueOrDefault(node) ?? [])
                {
                    if (edge.Source != split && !region.Contains(edge.Source))
                    {
                        error("parallel.enter",
                            $"В блок {name(node)} внутри параллельных ветвей входит связь снаружи (из {name(edge.Source)}). " +
                            "Ветви начинаются только с блока «Параллельно».", node);
                        ok = false;
                    }
                }
            }
        }

        stack.Remove(split);
        if (!ok || join is null)
        {
            return _resolved[split] = null;
        }

        _joinOwner[join] = split;
        return _resolved[split] = (join, region);
    }

    /// <summary>Обойти ветвь до слияния (вложенные участки — целиком), собрать её блоки; вернуть встреченные слияния.</summary>
    private HashSet<string> Walk(string start, string split, int branch, HashSet<string> region, Dictionary<string, int> branchOf,
        HashSet<string> stack, ref bool ok)
    {
        var joins = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == split)
            {
                error("parallel.loop",
                    $"Ветвь блока {name(split)} возвращается к нему самому. Повторить весь параллельный участок можно только связью от " +
                    "его слияния назад.", split);
                ok = false;
                continue;
            }

            if (!visited.Add(node) || !descriptors.TryGetValue(node, out var descriptor))
            {
                continue;
            }

            if (descriptor.Flow == NodeFlow.Join)
            {
                joins.Add(node);
                continue;
            }

            // «Завершение» заканчивает весь процесс: его можно делить между ветвями и основной схемой.
            if (descriptor.IsEnd)
            {
                continue;
            }

            if (branchOf.TryGetValue(node, out var owner) && owner != branch)
            {
                error("parallel.merge.early",
                    $"Ветви блока {name(split)} сходятся в блоке {name(node)} раньше слияния — соедините их блоком «Дождаться всех ветвей».", node);
                ok = false;
                continue;
            }

            branchOf[node] = branch;
            region.Add(node);
            if (descriptor.Flow == NodeFlow.Split)
            {
                // Вложенный участок — целиком часть этой ветви; дальше — от его слияния.
                if (Resolve(node, stack) is { } inner)
                {
                    foreach (var member in inner.Region)
                    {
                        branchOf[member] = branch;
                        region.Add(member);
                    }

                    branchOf[inner.Join] = branch;
                    region.Add(inner.Join);
                    foreach (var edge in _outgoing.GetValueOrDefault(inner.Join) ?? [])
                    {
                        queue.Enqueue(edge.Target);
                    }
                }
                else
                {
                    ok = false;
                }

                continue;
            }

            foreach (var edge in _outgoing.GetValueOrDefault(node) ?? [])
            {
                queue.Enqueue(edge.Target);
            }
        }

        return joins;
    }
}
