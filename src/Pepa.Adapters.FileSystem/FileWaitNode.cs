using System.Text.Json;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.FileSystem;

/// <summary>Ждать, пока в отслеживаемой папке появится файл (событие «files» от слежения за папками).</summary>
public sealed class WaitFileNode(IWorkspace workspace, Func<IReadOnlyList<FolderWatchSettings>> watches) : INodeExecutor
{
    public const string TypeName = "file.wait";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Дождаться файла",
        Description = "Ждёт, пока в папке появится файл (например, скачанный отчёт или ответ другой программы). Папка должна " +
                      "быть в «Настройки» → «Слежение за папками». Файл засчитывается, когда он дописан. Ожидание переживает " +
                      "закрытие Pepa.",
        Icon = "file-clock",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "folder",
        Outputs =
        [
            new PortDescriptor(Ports.Event, "Появился"),
            new PortDescriptor(Ports.Timeout, "Тайм-аут", PortKind.Optional),
            new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error),
        ],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "folder", Label = "Папка", Kind = FieldKind.Path, Required = true, Placeholder = "входящие", Lookup = "watched-folders",
                Help = "Папка из «Настройки» → «Слежение за папками».",
            },
            new FieldDescriptor
            {
                Name = "name", Label = "Имя файла", Kind = FieldKind.Template, Placeholder = "*.pdf",
                Help = "Необязательно: имя или шаблон («*» — любые символы, «?» — один символ, несколько — через «;»), " +
                       "например {{ vars.order }}-*.pdf (значение переменной — часть имени, не шаблон). Пусто — любой файл, " +
                       "за которым следит Pepa в этой папке.",
            },
            new FieldDescriptor
            {
                Name = "changes", Label = "Также изменённые файлы", Kind = FieldKind.Boolean, Default = false,
                Help = "Засчитывать и изменение уже лежавшего файла (в слежении за папкой должно быть включено то же).",
            },
            new FieldDescriptor
            {
                Name = "timeoutSeconds", Label = "Ждать не дольше, секунд", Kind = FieldKind.Seconds, Min = 0, Max = 31_536_000,
                Placeholder = "без ограничения",
            },
            new FieldDescriptor
            {
                Name = "lookbackSeconds", Label = "Учитывать появившиеся раньше, секунд", Kind = FieldKind.Seconds, Default = 60,
                Min = 0, Max = 86_400,
                Help = "Файл мог появиться, пока процесс выполнял предыдущий шаг. Берутся только файлы, которые ещё никто не забрал.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить событие в", Kind = FieldKind.Variable },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var folder = workspace.ToRelative(workspace.Resolve(context.GetString("folder")));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var watch = watches().FirstOrDefault(w => w.Enabled && string.Equals(w.Folder, folder, comparison))
            ?? throw new NodeInputException(
                $"За папкой «{folder}» Pepa не следит — добавьте её в «Настройки» → «Слежение за папками» (или включите слежение), " +
                "иначе процесс ждал бы вечно.");
        var changes = context.GetBoolean("changes") == true;
        if (changes && !watch.Changes)
        {
            throw new NodeInputException(
                $"В слежении за папкой «{folder}» не включено «Также изменённые файлы» — об изменениях Pepa не сообщит.");
        }

        var configuredName = context.Node.Config.GetString("name");
        var name = context.GetString("name")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredName) && string.IsNullOrEmpty(name))
        {
            throw new NodeInputException($"Имя файла «{configuredName}» получилось пустым — иначе подошёл бы любой файл. Проверьте переменные.");
        }

        string? match = null;
        if (!string.IsNullOrEmpty(name))
        {
            if (FileNamePattern.Validate(name) is { } error)
            {
                throw new NodeInputException(error);
            }

            // «*», «?» и «;» — только те, что написаны в самом шаблоне: значение из переменной («Ромашка; ООО») — часть имени, а
            // в именах файлов «*» и «?» не бывает — такое значение подобрало бы чужие файлы.
            if (configuredName is { } template && TemplateParser.HasExpressions(template))
            {
                var literal = string.Concat(TemplateParser.Parse(template).OfType<TemplateParser.Literal>().Select(l => l.Text));
                if ("*?;".Any(c => name.Count(x => x == c) > literal.Count(x => x == c)))
                {
                    throw new NodeInputException(
                        $"В имени файла «{name}» из переменной есть «*», «?» или «;» — такого имени файла не бывает, а как шаблон оно " +
                        "подобрало бы чужие файлы. Проверьте значение переменной.");
                }
            }

            match = $"new RegExp({JsonSerializer.Serialize(FileNamePattern.RegexSource(name))}, \"i\").test(String(event.payload.name ?? \"\"))";
        }

        var timeout = context.GetNumber("timeoutSeconds") is { } seconds and > 0
            ? context.Time.GetUtcNow() + TimeSpan.FromSeconds(seconds)
            : (DateTimeOffset?)null;
        return ValueTask.FromResult<NodeResult>(new NodeResult.WaitForEvent(new EventSubscriptionSpec
        {
            Source = FileEvents.Source,
            EventType = changes ? null : FileEvents.Created,
            CorrelationKey = watch.Folder,
            MatchExpression = match,
            LookbackSeconds = (int)(context.GetNumber("lookbackSeconds") ?? 60),
            SaveTo = context.GetString("saveTo") is { Length: > 0 } saveTo ? saveTo : null,
        }, timeout));
    }
}
