using System.Text.Json.Nodes;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables;

/// <summary>
/// «Добавить строку»: строка после последней заполненной. Нет файла — он создаётся (со строкой названий из значений).
/// После сбоя строка не дописывается второй раз: проверка ищет её среди строк, появившихся после начала шага.
/// </summary>
public sealed class AppendRowNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "table.append";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TableNodes.Category.Id,
        Title = "Добавить строку в таблицу",
        Description = "Дописывает строку в таблицу Excel или CSV после последней заполненной. Если файла нет — создаёт его " +
                      "с названиями колонок из значений. Таблица, открытая в Excel, не меняется — сначала её нужно закрыть.",
        Icon = "grid-2x2-plus",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [new PortDescriptor(Ports.Out, "Далее"), TableNodes.Error],
        Fields =
        [
            TableNodes.PathField,
            TableNodes.SheetField,
            TableNodes.HeaderField,
            TableNodes.ValuesField("({ Логин: vars.login, Статус: 'готово', Дата: vars.today })"),
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        if (!file.Exists)
        {
            return ValueTask.FromResult<JsonObject?>(new JsonObject { ["exists"] = false });
        }

        var sheet = file.Read(context.GetString("sheet")).Sheet;
        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["exists"] = true, ["lastRow"] = sheet.LastRow });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        using var turn = await file.LockAsync(cancellationToken);
        if (turn is null)
        {
            return NodeResult.Fail($"Не дождались очереди к таблице «{file.Relative}» — строка не добавлена.");
        }

        var header = context.GetBoolean("header", true);
        var sheetName = context.GetString("sheet");
        var snapshot = file.Exists ? file.Read(sheetName) : null;
        var sheet = snapshot?.Sheet ?? file.New(sheetName);
        var (row, headerRow, changes) = Plan(sheet, header, context.Input["values"]);
        try
        {
            file.Write(snapshot, sheetName, changes, headerRow);
        }
        catch (TableBusyException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            TableNodes.Output(file, sheet, ("row", row), ("created", snapshot is null)),
            snapshot is null ? $"Создана таблица «{file.Relative}», добавлена строка {row}." : $"Добавлена строка {row}.");
    }

    /// <summary>
    /// Куда и что записать. В пустую таблицу с названиями колонок сначала пишется строка названий (из значений-объекта),
    /// затем сама строка.
    /// </summary>
    private static (int Row, int HeaderRow, Dictionary<int, IReadOnlyDictionary<int, JsonNode?>> Changes) Plan(TableSheet sheet, bool header,
        JsonNode? values)
    {
        var changes = new Dictionary<int, IReadOnlyDictionary<int, JsonNode?>>();
        IReadOnlyList<(TableColumn Column, JsonNode? Value)> cells;
        int row;
        int headerRow;
        if (header && sheet.LastRow == 0)
        {
            if (values is not JsonObject { Count: > 0 } map)
            {
                throw new NodeInputException("Таблица пуста: чтобы появилась строка названий колонок, значения — объект «колонка: значение».");
            }

            var names = new Dictionary<int, JsonNode?>();
            var index = 1;
            foreach (var (name, _) in map)
            {
                if (name.Trim().Length == 0)
                {
                    throw new NodeInputException("Пустое название колонки.");
                }

                names[index++] = name.Trim();
            }

            changes[1] = names;
            row = 2;
            headerRow = 1;
            var draft = new TableSheet { Name = sheet.Name, Csv = sheet.Csv };
            foreach (var (column, name) in names)
            {
                draft.Set(1, column, name);
            }

            cells = TableLayout.Of(draft, header: true).Resolve(values);
        }
        else
        {
            row = sheet.LastRow + 1;
            var layout = TableLayout.Of(sheet, header);
            headerRow = layout.HeaderRow;
            cells = layout.Resolve(values);
        }

        if (row > TableValues.MaxRow)
        {
            throw new NodeInputException($"В листе Excel не больше {TableValues.MaxRow} строк.");
        }

        var data = cells.Where(c => !TableValues.IsEmpty(c.Value)).ToDictionary(c => c.Column.Index, c => c.Value);
        if (data.Count == 0)
        {
            throw new NodeInputException("Все значения пустые — добавлять нечего.");
        }

        changes[row] = data;
        return (row, headerRow, changes);
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        var existed = context.Checkpoint?.GetBoolean("exists") == true;
        if (!file.Exists)
        {
            return ValueTask.FromResult(existed
                ? VerificationResult.Inconclusive($"таблицы «{file.Relative}» больше нет.")
                : VerificationResult.NotDone($"таблицы «{file.Relative}» нет — строка не добавлена."));
        }

        TableSheet sheet;
        IReadOnlyList<(TableColumn Column, JsonNode? Value)> cells;
        TableLayout layout;
        try
        {
            sheet = file.Read(context.GetString("sheet")).Sheet;
            layout = TableLayout.Of(sheet, context.GetBoolean("header", true));
            cells = layout.Resolve(context.Input["values"]);
        }
        catch (NodeInputException ex)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive($"таблицу не удалось сверить: {ex.Message}"));
        }

        var before = existed ? (int)(context.Checkpoint?.GetNumber("lastRow") ?? 0) : 0;
        if (sheet.LastRow < before)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive($"в таблице «{file.Relative}» стало меньше строк, чем до действия."));
        }

        var filled = cells.Where(c => !TableValues.IsEmpty(c.Value)).ToList();
        var matches = layout.DataRows
            .Where(r => r > before && filled.All(c => TableValues.Same(sheet.Cell(r, c.Column.Index), sheet.Stored(c.Value))))
            .ToList();
        return ValueTask.FromResult(matches.Count switch
        {
            0 => VerificationResult.NotDone($"после строки {before} такой строки в «{file.Relative}» нет."),
            1 => VerificationResult.Done($"строка {matches[0]} в «{file.Relative}» уже добавлена."),
            _ => VerificationResult.Inconclusive(
                $"после строки {before} в «{file.Relative}» таких строк несколько ({TableNodes.RowList(matches)}) — какая из них добавлена этим шагом, неясно."),
        });
    }
}

/// <summary>
/// «Изменить строку»: найти строку по значению в колонке и записать в неё новые значения. Несколько подходящих строк —
/// ошибка, а не первая попавшаяся.
/// </summary>
public sealed class UpdateRowNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "table.update";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TableNodes.Category.Id,
        Title = "Изменить строку таблицы",
        Description = "Находит строку, где в колонке — нужное значение, и записывает в неё новые значения (остальные ячейки " +
                      "не меняются). Например, отметить «готово» напротив аккаунта.",
        Icon = "grid-2x2-check",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "value",
        Outputs = [new PortDescriptor(Ports.Out, "Изменена"), TableNodes.NotFound, TableNodes.Error],
        Fields =
        [
            TableNodes.PathField,
            TableNodes.SheetField,
            TableNodes.HeaderField,
            TableNodes.ColumnField,
            TableNodes.ValueField,
            TableNodes.ValuesField("({ Статус: 'готово', Проверено: vars.today })"),
        ],
    };

    private sealed record Target(TableFile File, TableSnapshot Snapshot, TableLayout Layout, TableColumn Column, string Value, List<int> Rows);

    private Target Find(NodeExecutionContext context)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        var snapshot = file.Read(context.GetString("sheet"));
        var layout = TableLayout.Of(snapshot.Sheet, context.GetBoolean("header", true));
        var column = TableNodes.RequireColumn(layout, context.GetString("column"), file);
        var value = context.GetString("value") ?? "";
        return new Target(file, snapshot, layout, column, value, layout.Match(column, value));
    }

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var target = Find(context);
        if (target.Rows is not [var row])
        {
            return ValueTask.FromResult<JsonObject?>(new JsonObject { ["row"] = null });
        }

        var before = new JsonObject();
        foreach (var (column, _) in target.Layout.Resolve(context.Input["values"]))
        {
            before[column.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = target.Snapshot.Sheet.Cell(row, column.Index)?.DeepClone();
        }

        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["row"] = row, ["before"] = before });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var file = TableFile.Open(workspace, context.GetString("path"));
        using var turn = await file.LockAsync(cancellationToken);
        if (turn is null)
        {
            return NodeResult.Fail($"Не дождались очереди к таблице «{file.Relative}» — строка не изменена.");
        }

        var target = Find(context);
        var sheet = target.Snapshot.Sheet;
        var where = TableNodes.Where(target.Column, target.Value);
        switch (target.Rows.Count)
        {
            case 0:
                return NodeResult.Ok(Ports.False, new Dictionary<string, JsonNode?>(),
                    TableNodes.Output(file, sheet, ("found", 0)), $"Строка с {where} не найдена — ничего не изменено.");
            case > 1:
                return NodeResult.Fail(
                    $"В таблице «{file.Relative}» {target.Rows.Count} строк с {where} (строки {TableNodes.RowList(target.Rows)}) — " +
                    "какую менять, неясно. Ничего не изменено.", retryable: false);
        }

        var row = target.Rows[0];
        if (context.Checkpoint?.GetNumber("row") is not { } planned || (int)planned != row)
        {
            // Строка сдвинулась (или нашлась) между подготовкой и действием: снимок «до» не о ней — подготовить заново.
            return NodeResult.Fail($"Таблица «{file.Relative}» изменилась, пока шаг готовился, — ничего не изменено.");
        }

        var cells = target.Layout.Resolve(context.Input["values"]);
        var output = TableNodes.Output(file, sheet, ("found", 1), ("row", row));
        if (cells.All(c => TableValues.Same(sheet.Cell(row, c.Column.Index), sheet.Stored(c.Value))))
        {
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"В строке {row} уже эти значения.");
        }

        try
        {
            file.Write(target.Snapshot, context.GetString("sheet"),
                new Dictionary<int, IReadOnlyDictionary<int, JsonNode?>> { [row] = cells.ToDictionary(c => c.Column.Index, c => c.Value) },
                target.Layout.HeaderRow);
        }
        catch (TableBusyException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"Изменена строка {row}.");
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Checkpoint?.GetNumber("row") is not { } planned)
        {
            return ValueTask.FromResult(VerificationResult.NotDone("строка для изменения не была найдена — ничего не менялось."));
        }

        var row = (int)planned;
        TableSheet sheet;
        IReadOnlyList<(TableColumn Column, JsonNode? Value)> cells;
        try
        {
            var file = TableFile.Open(workspace, context.GetString("path"));
            sheet = file.Read(context.GetString("sheet")).Sheet;
            cells = TableLayout.Of(sheet, context.GetBoolean("header", true)).Resolve(context.Input["values"]);
        }
        catch (NodeInputException ex)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive($"таблицу не удалось сверить: {ex.Message}"));
        }

        var before = context.Checkpoint?["before"] as JsonObject;
        string Key(TableColumn column) => column.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (cells.All(c => TableValues.Same(sheet.Cell(row, c.Column.Index), sheet.Stored(c.Value))))
        {
            return ValueTask.FromResult(VerificationResult.Done($"в строке {row} уже новые значения."));
        }

        return ValueTask.FromResult(before is not null && cells.All(c => TableValues.Same(sheet.Cell(row, c.Column.Index), before[Key(c.Column)]))
            ? VerificationResult.NotDone($"в строке {row} прежние значения.")
            : VerificationResult.Inconclusive($"в строке {row} не прежние и не новые значения — её, возможно, изменил кто-то ещё."));
    }
}
