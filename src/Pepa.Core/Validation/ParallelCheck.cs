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
    IReadOnlyDictionary<string, NodeDefinition> nodes,
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

    /// <summary>Блок, после которого — только путь к «Завершению» (без слияний и разделений): его могут делить ветви.</summary>
    private readonly Dictionary<string, bool> _endsOnly = new(StringComparer.Ordinal);

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
        CheckSharedState(split, branchOf);
        return _resolved[split] = (join, region);
    }

    /// <summary>Блоки разных ветвей не должны работать с одним общим состоянием (одним окном браузера).</summary>
    private void CheckSharedState(string split, Dictionary<string, int> branchOf)
    {
        var users = new Dictionary<string, (HashSet<int> Branches, List<string> Nodes)>(StringComparer.Ordinal);
        foreach (var (node, branch) in branchOf)
        {
            if (!descriptors.TryGetValue(node, out var descriptor) || !nodes.TryGetValue(node, out var definition))
            {
                continue;
            }

            foreach (var field in descriptor.Fields.Where(f => f.SharedState is not null))
            {
                var value = (definition.Config[field.Name] is System.Text.Json.Nodes.JsonValue configured && configured.TryGetValue<string>(out var text)
                    ? text
                    : field.Default?.ToString()) ?? "";
                value = value.Trim();
                if (value.Contains("{{", StringComparison.Ordinal))
                {
                    // Значение вычисляется при выполнении — заранее не проверить.
                    continue;
                }

                var key = $"{field.SharedState}\u0000{value}";
                if (!users.TryGetValue(key, out var entry))
                {
                    users[key] = entry = ([], []);
                }

                entry.Branches.Add(branch);
                entry.Nodes.Add(node);
            }
        }

        foreach (var (key, entry) in users.Where(u => u.Value.Branches.Count > 1))
        {
            var value = key[(key.IndexOf('\u0000') + 1)..];
            error("parallel.shared.state",
                $"Параллельные ветви блока {name(split)} работают с одним окном браузера «{value}» ({string.Join(", ", entry.Nodes.Take(4).Select(name))}): " +
                "шаги ветвей перемешались бы на одной вкладке. Дайте ветвям разные окна (поле «Окно браузера») или перенесите шаги браузера " +
                "до или после параллельного участка.", entry.Nodes[0]);
        }
    }

    /// <summary>После блока — только путь к «Завершению»: обычные блоки без слияний, разделений и циклов.</summary>
    private bool EndsOnly(string node, HashSet<string>? visiting = null)
    {
        if (_endsOnly.TryGetValue(node, out var known))
        {
            return known;
        }

        if (!descriptors.TryGetValue(node, out var descriptor))
        {
            return false;
        }

        if (descriptor.IsEnd)
        {
            return _endsOnly[node] = true;
        }

        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        if (descriptor.Flow != NodeFlow.Normal || !visiting.Add(node))
        {
            return false;
        }

        var outgoing = _outgoing.GetValueOrDefault(node) ?? [];
        var result = outgoing.Count > 0 && outgoing.All(e => EndsOnly(e.Target, visiting));
        visiting.Remove(node);
        return _endsOnly[node] = result;
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

            // «Завершение» заканчивает весь процесс: его можно делить между ветвями и основной схемой — как и путь, который
            // ведёт только к нему (например, общий обработчик ошибок «Записать в журнал» → «Завершение»).
            if (descriptor.IsEnd || EndsOnly(node))
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
