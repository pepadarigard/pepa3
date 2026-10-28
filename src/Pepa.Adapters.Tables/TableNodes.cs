using System.Text.Json.Nodes;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables;

public static class TableNodes
{
    public static readonly NodeCategory Category = new("tables", "Таблицы (Excel, CSV)", 12);

    public static NodeRegistry RegisterTableNodes(this NodeRegistry registry, IWorkspace workspace) => registry
        .RegisterCategory(Category)
        .Register(new ReadTableNode(workspace))
        .Register(new FindRowNode(workspace))
        .Register(new AppendRowNode(workspace))
        .Register(new UpdateRowNode(workspace));

    internal static readonly PortDescriptor Error = new(Ports.Error, "Ошибка", PortKind.Error);
    internal static readonly PortDescriptor NotFound = new(Ports.False, "Не найдена", PortKind.Optional);

    internal static FieldDescriptor PathField => new()
    {
        Name = "path", Label = "Таблица", Kind = FieldKind.Path, Required = true, Placeholder = "таблицы/клиенты.xlsx",
        Help = "Файл Excel (.xlsx, .xlsm) или CSV (.csv, .tsv) в рабочей папке.",
    };

    internal static FieldDescriptor SheetField => new()
    {
        Name = "sheet", Label = "Лист", Kind = FieldKind.Template, Placeholder = "Лист1",
        Help = "Имя листа Excel; пусто — первый лист. У CSV листов нет.",
    };

    internal static FieldDescriptor HeaderField => new()
    {
        Name = "header", Label = "Первая строка — названия колонок", Kind = FieldKind.Boolean, Default = true,
        Help = "Иначе колонки называются буквами, как в Excel: A, B, C.",
    };

    internal static FieldDescriptor ColumnField => new()
    {
        Name = "column", Label = "Колонка для поиска", Kind = FieldKind.Template, Required = true, Placeholder = "Телефон",
        Help = "Название колонки (или буква, если у таблицы нет строки названий).",
    };

    internal static FieldDescriptor ValueField => new()
    {
        Name = "value", Label = "Значение в ней", Kind = FieldKind.Template, Required = true, Placeholder = "{{ vars.phone }}",
        Help = "Сравнивается без учёта регистра и пробелов по краям; числа — по значению (91,5 и 91.5 — одно и то же).",
    };

    internal static FieldDescriptor ValuesField(string placeholder) => new()
    {
        Name = "values", Label = "Значения", Kind = FieldKind.Expression, Required = true, Placeholder = placeholder,
        Help = "Объект «колонка: значение» — названия колонок как в таблице. Значение — текст, число или да/нет; " +
               "пусто (null) — ячейка пустая. Дата вида 2026-10-27 в колонке с форматом даты станет датой Excel. " +
               "Формулы Pepa не пишет: текст, начинающийся с «=», останется текстом.",
    };

    internal static JsonObject Output(TableFile file, TableSheet sheet, params (string Key, JsonNode? Value)[] values)
    {
        var result = new JsonObject { ["path"] = file.Relative };
        if (sheet.Name.Length > 0)
        {
            result["sheet"] = sheet.Name;
        }

        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }

    internal static TableColumn RequireColumn(TableLayout layout, string? name, TableFile file) =>
        layout.Find(name ?? "") ?? throw new NodeInputException(layout.HeaderRow == 0
            ? $"«{name}» — не буква колонки (A, B, … — у таблицы без строки названий)."
            : $"В таблице «{file.Relative}» нет колонки «{name}»: {layout.ColumnList()}.");

    internal static string Where(TableColumn column, string value) => $"«{column.Name}» = «{value.Trim()}»";

    /// <summary>
    /// Искомое значение. Пустое — ошибка: так получается из опечатки в подстановке ({{ vars.row.Логн }}), и иначе нашлась бы
    /// строка с пустой ячейкой (например, «Итого»).
    /// </summary>
    internal static string RequireValue(NodeExecutionContext context) =>
        context.GetString("value") is { } value && value.Trim().Length > 0
            ? value
            : throw new NodeInputException("Значение для поиска пустое — проверьте подстановку в поле «Значение в ней».");

    internal static string RowList(IReadOnlyList<int> rows) =>
        string.Join(", ", rows.Take(10)) + (rows.Count > 10 ? " …" : "");
}

/// <summary>«Прочитать таблицу»: строки листа — списком в переменную (для цикла «Для каждого»).</summary>
public sealed class ReadTableNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "table.read";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TableNodes.Category.Id,
        Title = "Прочитать таблицу",
        Description = "Читает строки таблицы Excel или CSV в переменную-список: у каждой строки — значения по названиям колонок. " +
                      "Например, список аккаунтов для цикла «Для каждого».",
        Icon = "sheet",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "path",
        Outputs = [new PortDescriptor(Ports.Out, "Далее"), TableNodes.Error],
        Fields =
        [
            TableNodes.PathField,
            TableNodes.SheetField,
            TableNodes.HeaderField,
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить строки в", Kind = FieldKind.Variable, Required = true,
                Help = "Список строк. В цикле «Для каждого» — {{ vars.row.Телефон }}, где row — переменная элемента. " +
                       "Пустые строки пропускаются; дата из Excel — текстом 2026-10-27.",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        TableSheet sheet;
        try
        {
            sheet = file.Read(context.GetString("sheet")).Sheet;
        }
        catch (TableBusyException ex)
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail(ex.Message));
        }

        var layout = TableLayout.Of(sheet, context.GetBoolean("header", true));
        if (layout.DataRows.Count > TableValues.MaxRows)
        {
            throw new NodeInputException($"В таблице «{file.Relative}» больше {TableValues.MaxRows:N0} строк — разделите её на части.");
        }

        var rows = new JsonArray([.. layout.DataRows.Select(layout.RowObject)]);
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out,
            new Dictionary<string, JsonNode?> { [context.RequireString("saveTo")] = rows },
            TableNodes.Output(file, sheet, ("rows", rows.Count), ("columns", new JsonArray([.. layout.Columns.Select(c => (JsonNode)c.Name)]))),
            $"Прочитано строк: {rows.Count}."));
    }
}

/// <summary>«Найти строку»: строка, где в колонке — нужное значение (например, пароль по логину).</summary>
public sealed class FindRowNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "table.find";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TableNodes.Category.Id,
        Title = "Найти строку в таблице",
        Description = "Ищет строку, где в колонке — нужное значение, и сохраняет её в переменную. Например, данные аккаунта по логину.",
        Icon = "file-search",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "value",
        Outputs = [new PortDescriptor(Ports.True, "Найдена"), TableNodes.NotFound, TableNodes.Error],
        Fields =
        [
            TableNodes.PathField,
            TableNodes.SheetField,
            TableNodes.HeaderField,
            TableNodes.ColumnField,
            TableNodes.ValueField,
            new FieldDescriptor
            {
                Name = "which", Label = "Если подходит несколько строк", Kind = FieldKind.Select, Default = "only",
                Options = [new("only", "Ошибка — нужна одна"), new("first", "Взять первую"), new("all", "Взять все (список)")],
                Help = "По умолчанию несколько подходящих строк — ошибка: Pepa не угадывает, какая из них нужна.",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить строку в", Kind = FieldKind.Variable, Required = true,
                Help = "Значения по названиям колонок: {{ vars.account.Пароль }}.",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        var value = TableNodes.RequireValue(context);
        var which = context.GetString("which") ?? "only";
        if (which is not ("only" or "first" or "all"))
        {
            throw new NodeInputException($"«{which}» — не вариант поля «Если подходит несколько строк».");
        }

        TableSheet sheet;
        try
        {
            sheet = file.Read(context.GetString("sheet")).Sheet;
        }
        catch (TableBusyException ex)
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail(ex.Message));
        }

        var layout = TableLayout.Of(sheet, context.GetBoolean("header", true));
        var column = TableNodes.RequireColumn(layout, context.GetString("column"), file);
        var rows = layout.Match(column, value);
        var where = TableNodes.Where(column, value);
        if (rows.Count == 0)
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.False, new Dictionary<string, JsonNode?>(),
                TableNodes.Output(file, sheet, ("found", 0)), $"Строка с {where} не найдена."));
        }

        if (rows.Count > 1 && which == "only")
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail(
                $"В таблице «{file.Relative}» {rows.Count} строк с {where} (строки {TableNodes.RowList(rows)}) — какая нужна, неясно. " +
                "Уточните поиск или выберите «Взять первую».", retryable: false));
        }

        JsonNode found = which == "all" ? new JsonArray([.. rows.Select(layout.RowObject)]) : layout.RowObject(rows[0]);
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.True,
            new Dictionary<string, JsonNode?> { [context.RequireString("saveTo")] = found },
            TableNodes.Output(file, sheet, ("found", rows.Count), ("row", which == "all" ? new JsonArray([.. rows.Select(r => (JsonNode)r)]) : rows[0])),
            which == "all" ? $"Найдено строк: {rows.Count}." : $"Найдена строка {rows[0]}."));
    }
}
