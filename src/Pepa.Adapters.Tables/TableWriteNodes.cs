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

        var snapshot = file.Read(context.GetString("sheet"));
        return ValueTask.FromResult<JsonObject?>(new JsonObject
        {
            ["exists"] = true, ["lastRow"] = snapshot.Sheet.LastRow, ["hash"] = snapshot.Hash,
        });
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
        TableSnapshot? snapshot;
        try
        {
            snapshot = file.Exists ? file.Read(sheetName) : null;
        }
        catch (TableBusyException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        var sheet = snapshot?.Sheet ?? file.New(sheetName);
        var (row, headerRow, changes) = Plan(sheet, header, context.Input["values"]);
        try
        {
            file.Write(snapshot, sheetName, changes, headerRow, append: true);
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

    /// <summary>
    /// Добавлена ли строка. «Не выполнено» — только если таблица с начала шага не изменилась вовсе (иначе строку могли
    /// переставить при сортировке или пересохранить по-другому — и повтор добавил бы её второй раз). «Выполнено» — если
    /// среди строк, появившихся после начала шага, ровно одна такая, а выше таких не было: одинаковые строки (свою от
    /// чужой) проверка не различает, тогда решает пользователь.
    /// </summary>
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

        TableSnapshot snapshot;
        try
        {
            snapshot = file.Read(context.GetString("sheet"));
        }
        catch (Exception ex) when (ex is NodeInputException or TableBusyException)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive($"таблицу не удалось сверить: {ex.Message}"));
        }

        if (existed && snapshot.Hash == context.Checkpoint?.GetString("hash"))
        {
            return ValueTask.FromResult(VerificationResult.NotDone($"таблица «{file.Relative}» не менялась с начала шага — строка не добавлена."));
        }

        var sheet = snapshot.Sheet;
        IReadOnlyList<(TableColumn Column, JsonNode? Value)> cells;
        TableLayout layout;
        try
        {
            layout = TableLayout.Of(sheet, context.GetBoolean("header", true));
            cells = layout.Resolve(context.Input["values"]);
        }
        catch (NodeInputException ex)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive($"таблицу не удалось сверить: {ex.Message}"));
        }

        var before = existed ? (int)(context.Checkpoint?.GetNumber("lastRow") ?? 0) : 0;
        var filled = cells.Where(c => !TableValues.IsEmpty(c.Value)).ToList();
        var same = layout.DataRows.Where(r => filled.All(c => sheet.Holds(r, c.Column.Index, c.Value))).ToList();
        var added = same.Where(r => r > before).ToList();
        if (added is [var row] && same.Count == 1)
        {
            return ValueTask.FromResult(VerificationResult.Done($"строка {row} в «{file.Relative}» уже добавлена."));
        }

        return ValueTask.FromResult(VerificationResult.Inconclusive(added.Count == 0
            ? $"таблица «{file.Relative}» изменилась после начала шага, а такой строки после строки {before} нет — " +
              "добавлена ли она (и, например, переставлена сортировкой), неясно."
            : $"в «{file.Relative}» таких строк несколько ({TableNodes.RowList(same)}) — добавил ли эту строку этот шаг, неясно."));
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
        var value = TableNodes.RequireValue(context);
        var snapshot = file.Read(context.GetString("sheet"));
        var layout = TableLayout.Of(snapshot.Sheet, context.GetBoolean("header", true));
        var column = TableNodes.RequireColumn(layout, context.GetString("column"), file);
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

        Target target;
        try
        {
            target = Find(context);
        }
        catch (TableBusyException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

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
        if (cells.All(c => sheet.Holds(row, c.Column.Index, c.Value)))
        {
            return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"В строке {row} уже эти значения.");
        }

        try
        {
            file.Write(target.Snapshot, context.GetString("sheet"),
                new Dictionary<int, IReadOnlyDictionary<int, JsonNode?>> { [row] = cells.ToDictionary(c => c.Column.Index, c => c.Value) },
                target.Layout.HeaderRow, append: false);
        }
        catch (TableBusyException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(), output, $"Изменена строка {row}.");
    }

    /// <summary>
    /// Изменена ли строка. Сначала — та ли это ещё запись: в строке по-прежнему искомое значение (или новое, если
    /// меняется сама колонка поиска); иначе таблицу отсортировали или изменили, и ответ — за пользователем.
    /// </summary>
    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Checkpoint?.GetNumber("row") is not { } planned)
        {
            return ValueTask.FromResult(VerificationResult.NotDone("строка для изменения не была найдена — ничего не менялось."));
        }

        var row = (int)planned;
        TableSheet sheet;
        TableColumn column;
        IReadOnlyList<(TableColumn Column, JsonNode? Value)> cells;
        try
        {
            var file = TableFile.Open(workspace, context.GetString("path"));
            sheet = file.Read(context.GetString("sheet")).Sheet;
            var layout = TableLayout.Of(sheet, context.GetBoolean("header", true));
            column = TableNodes.RequireColumn(layout, context.GetString("column"), file);
            cells = layout.Resolve(context.Input["values"]);
        }
        catch (Exception ex) when (ex is NodeInputException or TableBusyException)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive($"таблицу не удалось сверить: {ex.Message}"));
        }

        var newKey = cells.FirstOrDefault(c => c.Column.Index == column.Index);
        var sameRecord = TableValues.Matches(sheet.Cell(row, column.Index), context.GetString("value") ?? "") ||
                         (newKey.Column is not null && sheet.Holds(row, column.Index, newKey.Value));
        if (!sameRecord)
        {
            return ValueTask.FromResult(VerificationResult.Inconclusive(
                $"в строке {row} уже другая запись (нет «{(context.GetString("value") ?? "").Trim()}» в колонке «{column.Name}») — " +
                "таблицу, возможно, отсортировали или изменили."));
        }

        if (cells.All(c => sheet.Holds(row, c.Column.Index, c.Value)))
        {
            return ValueTask.FromResult(VerificationResult.Done($"в строке {row} уже новые значения."));
        }

        var before = context.Checkpoint?["before"] as JsonObject;
        string Key(TableColumn c) => c.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.FromResult(before is not null && cells.All(c => TableValues.Holds(sheet.Cell(row, c.Column.Index), before[Key(c.Column)]))
            ? VerificationResult.NotDone($"в строке {row} прежние значения.")
            : VerificationResult.Inconclusive($"в строке {row} не прежние и не новые значения — её, возможно, изменил кто-то ещё."));
    }
}
