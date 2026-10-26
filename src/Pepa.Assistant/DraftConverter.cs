using System.Globalization;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;

namespace Pepa.Assistant;

/// <summary>Результат разбора черновика: блоки Pepa и список ошибок (для исправления моделью или пользователем).</summary>
internal sealed record ConvertedDraft(
    IReadOnlyList<VariableDefinition> Variables,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges,
    string? EntryNodeId,
    IReadOnlyList<string> Problems);

/// <summary>
/// Ответ модели → блоки, связи и переменные Pepa. Значения полей приводятся к виду поля (число, да/нет), остальные поля
/// блока получают значения по умолчанию (как у блока, добавленного вручную). Затем черновик проверяется тем же
/// <see cref="ScenarioValidator"/>, что и сценарий в конструкторе, — как если бы он уже был вставлен после «Начала».
/// </summary>
internal sealed class DraftConverter(NodeRegistry registry, ScenarioValidator validator)
{
    private const string StartId = "__start";
    private const double ColumnWidth = 280;
    private const double RowHeight = 150;

    /// <param name="current">При правке — сценарий до изменений: неизменённые блоки остаются на своих местах.</param>
    public ConvertedDraft Convert(DraftJson draft, IReadOnlyList<VariableDefinition> existing, ScenarioDefinition? current = null)
    {
        var problems = new List<string>();

        // Переменные: новые (уже существующие не дублируются).
        var known = existing.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var variables = new List<VariableDefinition>();
        foreach (var v in draft.Variables)
        {
            var name = v.Name.Trim();
            if (known.Contains(name) || variables.Any(x => x.Name == name))
            {
                continue;
            }

            var type = Enum.TryParse<VariableType>(v.Type, ignoreCase: true, out var parsed) ? parsed : VariableType.Any;
            variables.Add(new VariableDefinition
            {
                Name = name,
                Type = type,
                Description = v.Description.Trim(),
                IsInput = v.IsInput,
                Default = DefaultValue(type, v.Default),
            });
        }

        // Блоки. При правке id «Начала» занят: блок с таким id совпал бы с ним на схеме.
        var previous = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var node in current?.Nodes ?? [])
        {
            previous.TryAdd(node.Id, node);
        }

        var nodes = new List<NodeDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (current?.Nodes.FirstOrDefault(n => n.Type == "start") is { } currentStart)
        {
            ids.Add(currentStart.Id);
        }

        foreach (var block in draft.Blocks)
        {
            var id = block.Id.Trim();
            var label = block.Label.Trim();
            var name = label.Length > 0 ? $"«{label}» ({id})" : id;
            if (id.Length == 0 || !ids.Add(id))
            {
                problems.Add(id.Length == 0 ? "У блока нет id." : $"id блока «{id}» повторяется — у каждого блока должен быть свой id.");
                continue;
            }

            var descriptor = registry.FindDescriptor(block.Type);
            if (descriptor is null || descriptor.IsStart)
            {
                problems.Add($"Блок {name}: тип «{block.Type}» нельзя использовать (нет в каталоге).");
                continue;
            }

            // Правка блока, который уже был: за основу — его поля и настройки (повторы, тайм-аут, точка останова), чтобы
            // не названное моделью не сбрасывалось к значениям по умолчанию.
            var old = previous.GetValueOrDefault(id) is { } before && before.Type == descriptor.Type ? before : null;
            var config = old is not null ? (JsonObject)old.Config.DeepClone() : new JsonObject();
            foreach (var field in descriptor.Fields)
            {
                if (field.Default is { } value && !config.ContainsKey(field.Name))
                {
                    config[field.Name] = value.DeepClone();
                }
            }

            foreach (var setting in block.Settings)
            {
                var field = descriptor.Fields.FirstOrDefault(f => f.Name == setting.Field.Trim());
                if (field is null)
                {
                    problems.Add($"Блок {name} ({descriptor.Type}): поля «{setting.Field}» нет. Поля этого блока: " +
                                 $"{string.Join(", ", descriptor.Fields.Select(f => f.Name))}.");
                    continue;
                }

                if (setting.Value.Length == 0)
                {
                    if (old is not null)
                    {
                        // Правка: пустое значение — поле очищено (текст — пустой, остальное — как по умолчанию).
                        if (FieldValue(field, "") is { } empty)
                        {
                            config[field.Name] = empty;
                        }
                        else if (field.Default is { } fallback)
                        {
                            config[field.Name] = fallback.DeepClone();
                        }
                        else
                        {
                            config.Remove(field.Name);
                        }
                    }

                    continue;
                }

                if (FieldValue(field, setting.Value) is { } converted)
                {
                    config[field.Name] = converted;
                }
                else
                {
                    problems.Add($"Блок {name}, поле «{field.Name}»: значение «{setting.Value}» не подходит (нужно {Expected(field)}).");
                }
            }

            nodes.Add(new NodeDefinition
            {
                Id = id,
                Type = descriptor.Type,
                Label = label.Length > 0 ? label : null,
                Note = block.Note.Trim() is { Length: > 0 } note ? note : null,
                Config = config,
                Settings = old?.Settings ?? new NodeSettings(),
            });
        }

        // Связи.
        var edges = new List<EdgeDefinition>();
        foreach (var link in draft.Links)
        {
            var from = nodes.FirstOrDefault(n => n.Id == link.From.Trim());
            var to = nodes.FirstOrDefault(n => n.Id == link.To.Trim());
            if (from is null || to is null)
            {
                problems.Add($"Связь {link.From} → {link.To}: блока «{(from is null ? link.From : link.To)}» нет в черновике.");
                continue;
            }

            var port = link.Port.Trim();
            var descriptor = registry.FindDescriptor(from.Type)!;
            if (descriptor.FindOutput(port) is null)
            {
                problems.Add($"Связь {link.From} → {link.To}: у блока {from.Id} ({from.Type}) нет выхода «{port}». Выходы: " +
                             $"{string.Join(", ", descriptor.Outputs.Select(p => p.Id))}.");
                continue;
            }

            if (edges.Any(e => e.Source == from.Id && e.SourcePort == port))
            {
                problems.Add($"Из выхода «{port}» блока {from.Id} выходит несколько связей — из выхода может идти только одна.");
                continue;
            }

            edges.Add(new EdgeDefinition { Id = $"{from.Id}-{port}-{to.Id}", Source = from.Id, SourcePort = port, Target = to.Id });
        }

        // Первый блок — без входящих связей (к нему подключится «Начало»).
        var targets = edges.Select(e => e.Target).ToHashSet(StringComparer.Ordinal);
        var entries = nodes.Where(n => !targets.Contains(n.Id)).ToList();
        var entry = entries.FirstOrDefault()?.Id;
        if (nodes.Count == 0)
        {
            problems.Add("В черновике нет ни одного блока.");
        }
        else if (entry is null)
        {
            problems.Add("У всех блоков есть входящие связи — непонятно, с какого блока начинать. Первый блок не должен иметь входящих связей.");
        }
        else if (entries.Count > 1)
        {
            problems.Add($"Без входящих связей несколько блоков ({string.Join(", ", entries.Select(n => n.Id))}): начинать можно только с одного, " +
                         "остальные должны быть к чему-то подключены.");
        }

        // Проверка как в конструкторе: черновик сразу после «Начала» (и при ошибках разбора — чтобы модель исправила всё
        // за один раз).
        if (entry is not null)
        {
            problems.AddRange(Validate(existing, variables, nodes, edges, entry).Where(p => !problems.Contains(p)));
        }

        return new ConvertedDraft(variables, current is null ? Layout(nodes, edges, entry) : Arrange(nodes, edges, entry, current), edges, entry,
            problems);
    }

    /// <summary>
    /// Правка: блоки, что были в сценарии, — на прежних местах; новые — справа от блока, за которым идут (ниже, если место
    /// занято); остальные — рядом под схемой.
    /// </summary>
    private static List<NodeDefinition> Arrange(List<NodeDefinition> nodes, List<EdgeDefinition> edges, string? entry, ScenarioDefinition current)
    {
        var before = new Dictionary<string, NodePosition>(StringComparer.Ordinal);
        foreach (var node in current.Nodes)
        {
            before.TryAdd(node.Id, node.Position);
        }

        var placed = nodes.Where(n => before.ContainsKey(n.Id)).ToDictionary(n => n.Id, n => before[n.Id], StringComparer.Ordinal);
        var links = edges.ToList();
        if (current.Nodes.FirstOrDefault(n => n.Type == "start") is { } start && entry is not null)
        {
            placed[start.Id] = start.Position;
            links.Add(new EdgeDefinition { Id = "__start", Source = start.Id, SourcePort = "out", Target = entry });
        }

        bool Taken(NodePosition p) => placed.Values.Any(q => Math.Abs(q.X - p.X) < 220 && Math.Abs(q.Y - p.Y) < 110);
        var pending = nodes.Where(n => !placed.ContainsKey(n.Id)).Select(n => n.Id).ToList();
        for (var progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            foreach (var id in pending.ToList())
            {
                if (links.FirstOrDefault(e => e.Target == id && placed.ContainsKey(e.Source)) is not { } link)
                {
                    continue;
                }

                var source = placed[link.Source];
                var spot = new NodePosition(source.X + ColumnWidth, source.Y);
                while (Taken(spot))
                {
                    spot = spot with { Y = spot.Y + RowHeight };
                }

                placed[id] = spot;
                pending.Remove(id);
                progress = true;
            }
        }

        var bottom = placed.Count == 0 ? 0 : placed.Values.Max(p => p.Y) + RowHeight;
        for (var i = 0; i < pending.Count; i++)
        {
            placed[pending[i]] = new NodePosition(i * ColumnWidth, bottom);
        }

        return nodes.Select(n => n with { Position = placed[n.Id] }).ToList();
    }

    private IEnumerable<string> Validate(IReadOnlyList<VariableDefinition> existing, List<VariableDefinition> variables,
        List<NodeDefinition> nodes, List<EdgeDefinition> edges, string entry)
    {
        var start = registry.Descriptors.First(d => d.IsStart);
        var scenario = new ScenarioDefinition
        {
            Name = "Черновик помощника",
            Variables = [.. existing, .. variables],
            Nodes = [new NodeDefinition { Id = StartId, Type = start.Type }, .. nodes],
            Edges = [new EdgeDefinition { Id = "__start-out", Source = StartId, SourcePort = start.Outputs[0].Id, Target = entry }, .. edges],
        };
        return validator.Validate(scenario).Issues
            .Where(i => i.Severity == IssueSeverity.Error && i.NodeId != StartId)
            .Select(i => i.Message)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Расстановка слева направо по шагам от первого блока; ветви — одна под другой.</summary>
    private static List<NodeDefinition> Layout(List<NodeDefinition> nodes, List<EdgeDefinition> edges, string? entry)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (entry is not null)
        {
            depth[entry] = 0;
            queue.Enqueue(entry);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var edge in edges.Where(e => e.Source == id))
            {
                if (!depth.ContainsKey(edge.Target))
                {
                    depth[edge.Target] = depth[id] + 1;
                    queue.Enqueue(edge.Target);
                }
            }
        }

        var lastColumn = depth.Count == 0 ? -1 : depth.Values.Max();
        var rows = new Dictionary<int, int>();
        var result = new List<NodeDefinition>();
        foreach (var node in nodes)
        {
            var column = depth.TryGetValue(node.Id, out var d) ? d : ++lastColumn;
            var row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            result.Add(node with { Position = new NodePosition(column * ColumnWidth, row * RowHeight) });
        }

        return result;
    }

    private static JsonNode? FieldValue(FieldDescriptor field, string value)
    {
        switch (field.Kind)
        {
            case FieldKind.Number or FieldKind.Seconds:
                // «NaN», «Infinity», «1e999» — не числа для поля: их не записать в сценарий.
                if (!double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number))
                {
                    return null;
                }

                return number % 1 == 0 && Math.Abs(number) < int.MaxValue ? JsonValue.Create((int)number) : JsonValue.Create(number);
            case FieldKind.Boolean:
                return value.Trim().ToLowerInvariant() switch
                {
                    "true" or "да" or "1" => JsonValue.Create(true),
                    "false" or "нет" or "0" => JsonValue.Create(false),
                    _ => null,
                };
            case FieldKind.Select when field.Options is { Count: > 0 } options:
                return options.FirstOrDefault(o => o.Value == value.Trim()) is { } option ? JsonValue.Create(option.Value) : null;
            default:
                return JsonValue.Create(value);
        }
    }

    private static string Expected(FieldDescriptor field) => field.Kind switch
    {
        FieldKind.Number or FieldKind.Seconds => "число",
        FieldKind.Boolean => "true или false",
        FieldKind.Select => $"одно из: {string.Join(", ", field.Options!.Select(o => o.Value))}",
        _ => "текст",
    };

    private static JsonNode? DefaultValue(VariableType type, string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return type == VariableType.Text ? JsonValue.Create("") : null;
        }

        return type switch
        {
            // Не число (или «бесконечность») — без значения по умолчанию, а не текст в числовой переменной.
            VariableType.Number => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                                   double.IsFinite(n)
                ? JsonValue.Create(n)
                : null,
            VariableType.Boolean when bool.TryParse(text, out var b) => JsonValue.Create(b),
            VariableType.List or VariableType.Object => TryParseJson(text),
            _ => JsonValue.Create(text),
        };
    }

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
