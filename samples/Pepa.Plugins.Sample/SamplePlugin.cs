using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Plugins;

namespace Pepa.Plugins.Sample;

/// <summary>Пример расширения: два блока — без внешних действий и с внешним действием (с проверкой результата).</summary>
public sealed class SamplePlugin : IPepaPlugin
{
    public static readonly NodeCategory Category = new("sample", "Пример расширения", 100);

    public string Name => "Пример расширения";

    public void Register(PluginRegistration registration) => registration
        .AddCategory(Category)
        .AddNode(new TextCaseNode())
        .AddNode(new AppendLineNode(registration.WorkspaceRoot));
}

/// <summary>Блок без внешних действий: повтор безопасен, проверять нечего.</summary>
public sealed class TextCaseNode : INodeExecutor
{
    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = "sample.textcase",
        Category = SamplePlugin.Category.Id,
        Title = "Изменить регистр текста",
        Description = "Делает текст прописными, строчными буквами или каждое слово с большой буквы.",
        Icon = "text-cursor-input",
        Effect = NodeEffect.None,
        SummaryField = "mode",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor { Name = "text", Label = "Текст", Kind = FieldKind.Template, Required = true },
            new FieldDescriptor
            {
                Name = "mode", Label = "Как", Kind = FieldKind.Select, Default = "upper",
                Options = [new("upper", "ПРОПИСНЫЕ"), new("lower", "строчные"), new("title", "Каждое Слово")],
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить в", Kind = FieldKind.Variable, Required = true },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var text = context.Input.GetString("text") ?? "";
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var result = context.Input.GetString("mode") switch
        {
            "lower" => text.ToLower(culture),
            "title" => culture.TextInfo.ToTitleCase(text.ToLower(culture)),
            _ => text.ToUpper(culture),
        };
        var updates = new Dictionary<string, JsonNode?> { [context.RequireString("saveTo")] = result };
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, updates, new JsonObject { ["text"] = result }));
    }
}

/// <summary>
/// Блок с внешним действием — дописать строку в файл рабочей папки. По правилам Pepa он умеет проверить результат: до
/// действия запоминает длину файла (PrepareAsync), а после сбоя смотрит, появилась ли строка после этого места (VerifyAsync),
/// — поэтому строка не допишется дважды.
/// </summary>
public sealed class AppendLineNode(Func<string> workspaceRoot) : INodeExecutor
{
    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = "sample.appendline",
        Category = SamplePlugin.Category.Id,
        Title = "Дописать строку в файл",
        Description = "Дописывает строку в конец текстового файла в рабочей папке (создаёт файл, если его нет).",
        Icon = "file-pen-line",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [new PortDescriptor(Ports.Out, "Далее"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        Fields =
        [
            new FieldDescriptor { Name = "path", Label = "Файл", Kind = FieldKind.Path, Required = true, Placeholder = "журнал.txt" },
            new FieldDescriptor { Name = "line", Label = "Строка", Kind = FieldKind.Template, Required = true },
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = FullPath(context);
        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["length"] = File.Exists(path) ? new FileInfo(path).Length : 0 });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = FullPath(context);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, Line(context) + Environment.NewLine, Encoding.UTF8, cancellationToken);
        return NodeResult.Ok(Ports.Out, $"Строка дописана в «{context.RequireString("path")}».");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = FullPath(context);
        var before = (long)(context.Checkpoint?.GetNumber("length") ?? 0);
        if (!File.Exists(path))
        {
            return VerificationResult.NotDone("файла нет — строка не дописана");
        }

        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var added = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text).AsSpan((int)Math.Min(before, Encoding.UTF8.GetByteCount(text))));
        return added.Contains(Line(context), StringComparison.Ordinal)
            ? VerificationResult.Done("строка есть в файле")
            : VerificationResult.NotDone("строки в файле нет — её можно дописать");
    }

    private static string Line(NodeExecutionContext context) => (context.Input.GetString("line") ?? "").ReplaceLineEndings(" ");

    /// <summary>Путь только внутри рабочей папки Pepa.</summary>
    private string FullPath(NodeExecutionContext context)
    {
        var root = Path.GetFullPath(workspaceRoot());
        var full = Path.GetFullPath(Path.Combine(root, context.RequireString("path")));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new NodeInputException("Файл должен быть внутри рабочей папки Pepa.");
    }
}
