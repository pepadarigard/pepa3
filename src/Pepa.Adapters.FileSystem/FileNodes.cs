using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.FileSystem;

public static class FileNodes
{
    public static NodeRegistry RegisterFileNodes(this NodeRegistry registry, IWorkspace workspace) => registry
        .RegisterCategory(NodeCategories.Files)
        .Register(new WriteFileNode(workspace))
        .Register(new ReadFileNode(workspace))
        .Register(new CopyFileNode(workspace))
        .Register(new MoveFileNode(workspace))
        .Register(new DeleteFileNode(workspace))
        .Register(new CreateFolderNode(workspace))
        .Register(new PathExistsNode(workspace))
        .Register(new ListFilesNode(workspace));

    internal static readonly PortDescriptor Next = new(Ports.Out, "Далее");
    internal static readonly PortDescriptor Error = new(Ports.Error, "Ошибка", PortKind.Error);

    internal static FieldDescriptor PathField(string name = "path", string label = "Файл", string? placeholder = "папка/файл.txt") => new()
    {
        Name = name,
        Label = label,
        Kind = FieldKind.Path,
        Required = true,
        Placeholder = placeholder,
        Help = "Путь внутри рабочей папки. Можно подставлять значения: отчёт-{{ vars.n }}.txt",
    };

    internal static JsonObject Output(params (string Key, JsonNode? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }
}

/// <summary>Записать текст в файл: перезаписать, создать только новый или дописать в конец.</summary>
public sealed class WriteFileNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.write";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Записать файл",
        Description = "Записывает текст в файл в рабочей папке. Папки по пути создаются автоматически.",
        Icon = "file-pen-line",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields =
        [
            FileNodes.PathField(),
            new FieldDescriptor
            {
                Name = "content", Label = "Текст", Kind = FieldKind.MultilineTemplate,
                Placeholder = "Привет! Сейчас обрабатывается {{ vars.name }}",
            },
            new FieldDescriptor
            {
                Name = "mode", Label = "Если файл уже есть", Kind = FieldKind.Select, Default = "overwrite",
                Options =
                [
                    new("overwrite", "Перезаписать"),
                    new("createNew", "Не трогать — ошибка"),
                    new("append", "Дописать в конец"),
                ],
            },
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["target"] = FileState.Snapshot(path) });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        var data = FileState.Utf8.GetBytes(context.GetString("content") ?? "");
        var mode = context.GetString("mode") ?? "overwrite";
        if (Directory.Exists(path))
        {
            return NodeResult.Fail($"По пути «{workspace.ToRelative(path)}» находится папка, а не файл.", retryable: false);
        }

        if (mode == "createNew" && File.Exists(path))
        {
            return NodeResult.Fail($"Файл «{workspace.ToRelative(path)}» уже существует.", retryable: false);
        }

        workspace.EnsureRootExists();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        cancellationToken.ThrowIfCancellationRequested();

        switch (mode)
        {
            case "append":
                await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    await stream.WriteAsync(data, CancellationToken.None);
                    stream.Flush(flushToDisk: true);
                }

                break;
            case "createNew":
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(data, CancellationToken.None);
                    stream.Flush(flushToDisk: true);
                }

                break;
            default:
                FileState.WriteAtomically(path, data);
                break;
        }

        return NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            FileNodes.Output(("path", workspace.ToRelative(path)), ("bytes", data.Length)));
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        var data = FileState.Utf8.GetBytes(context.GetString("content") ?? "");
        var before = context.Checkpoint?["target"] as JsonObject;
        var mode = context.GetString("mode") ?? "overwrite";
        var relative = workspace.ToRelative(path);
        var exists = File.Exists(path);

        return ValueTask.FromResult(mode switch
        {
            "append" => VerifyAppend(path, relative, data, before, exists),
            _ when !exists => VerificationResult.NotDone($"файла «{relative}» нет."),
            _ when FileState.Sha256(path) != FileState.Sha256(data) => mode == "createNew"
                ? VerificationResult.Inconclusive($"файл «{relative}» есть, но его содержимое отличается от записываемого.")
                : VerificationResult.NotDone($"файл «{relative}» не содержит записываемого текста."),
            _ when mode == "createNew" && FileState.Exists(before) =>
                VerificationResult.Inconclusive($"файл «{relative}» существовал и до действия."),
            _ => VerificationResult.Done($"файл «{relative}» содержит записанный текст."),
        });
    }

    private static VerificationResult VerifyAppend(string path, string relative, byte[] data, JsonObject? before, bool exists)
    {
        var existedBefore = FileState.Exists(before);
        if (!exists)
        {
            return existedBefore
                ? VerificationResult.Inconclusive($"файл «{relative}» был, а теперь его нет.")
                : VerificationResult.NotDone($"файла «{relative}» нет.");
        }

        var size = new FileInfo(path).Length;
        var sizeBefore = existedBefore ? FileState.Size(before) : 0;
        var hashBefore = existedBefore ? FileState.Hash(before) : FileState.Sha256([]);
        if (existedBefore && size == sizeBefore && FileState.Sha256(path) == hashBefore)
        {
            return VerificationResult.NotDone($"файл «{relative}» не изменился.");
        }

        if (size == sizeBefore + data.Length &&
            FileState.Sha256Prefix(path, sizeBefore) == hashBefore &&
            FileState.ReadTail(path, data.Length).AsSpan().SequenceEqual(data))
        {
            return VerificationResult.Done($"текст дописан в «{relative}» ровно один раз.");
        }

        return VerificationResult.Inconclusive($"файл «{relative}» изменился не так, как ожидалось.");
    }
}

public sealed class ReadFileNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.read";
    private const long MaxBytes = 10 * 1024 * 1024;

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Прочитать файл",
        Description = "Читает текст файла в переменную.",
        Icon = "file-text",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "path",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields =
        [
            FileNodes.PathField(),
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить в переменную", Kind = FieldKind.Variable, Required = true },
        ],
    };

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        var relative = workspace.ToRelative(path);
        if (!File.Exists(path))
        {
            return NodeResult.Fail($"Файл «{relative}» не найден.");
        }

        var length = new FileInfo(path).Length;
        if (length > MaxBytes)
        {
            return NodeResult.Fail($"Файл «{relative}» слишком большой ({length / 1024 / 1024} МБ). Предел — 10 МБ.", retryable: false);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return NodeResult.Ok(Ports.Out,
            new Dictionary<string, JsonNode?> { [context.RequireString("saveTo")] = text },
            FileNodes.Output(("path", relative), ("characters", text.Length)));
    }
}

public sealed class CopyFileNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.copy";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Копировать файл",
        Description = "Копирует файл внутри рабочей папки.",
        Icon = "copy",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "destination",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields =
        [
            FileNodes.PathField("source", "Откуда"),
            FileNodes.PathField("destination", "Куда", "архив/файл.txt"),
            new FieldDescriptor { Name = "overwrite", Label = "Заменить, если файл назначения уже есть", Kind = FieldKind.Boolean, Default = false },
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var source = workspace.Resolve(context.GetString("source"));
        var destination = workspace.Resolve(context.GetString("destination"));
        return ValueTask.FromResult<JsonObject?>(new JsonObject
        {
            ["source"] = FileState.Snapshot(source),
            ["destination"] = FileState.Snapshot(destination),
        });
    }

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var source = workspace.Resolve(context.GetString("source"));
        var destination = workspace.Resolve(context.GetString("destination"));
        if (TransferChecks.Validate(workspace, source, destination, context.GetBoolean("overwrite")) is { } failure)
        {
            return ValueTask.FromResult<NodeResult>(failure);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        cancellationToken.ThrowIfCancellationRequested();
        FileState.CopyAtomically(source, destination);
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            FileNodes.Output(("source", workspace.ToRelative(source)), ("destination", workspace.ToRelative(destination)))));
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var destination = workspace.Resolve(context.GetString("destination"));
        var sourceBefore = context.Checkpoint?["source"] as JsonObject;
        var destinationBefore = context.Checkpoint?["destination"] as JsonObject;
        var relative = workspace.ToRelative(destination);
        if (!File.Exists(destination))
        {
            return ValueTask.FromResult(VerificationResult.NotDone($"файла «{relative}» нет."));
        }

        var hash = FileState.Sha256(destination);
        if (hash == FileState.Hash(sourceBefore))
        {
            return ValueTask.FromResult(VerificationResult.Done($"«{relative}» совпадает с исходным файлом."));
        }

        return ValueTask.FromResult(FileState.Exists(destinationBefore) && hash == FileState.Hash(destinationBefore)
            ? VerificationResult.NotDone($"«{relative}» не изменился.")
            : VerificationResult.Inconclusive($"«{relative}» существует, но не совпадает с исходным файлом."));
    }
}

public sealed class MoveFileNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.move";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Переместить файл",
        Description = "Перемещает или переименовывает файл внутри рабочей папки.",
        Icon = "file-input",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "destination",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields =
        [
            FileNodes.PathField("source", "Откуда"),
            FileNodes.PathField("destination", "Куда", "готово/файл.txt"),
            new FieldDescriptor { Name = "overwrite", Label = "Заменить, если файл назначения уже есть", Kind = FieldKind.Boolean, Default = false },
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var source = workspace.Resolve(context.GetString("source"));
        var destination = workspace.Resolve(context.GetString("destination"));
        return ValueTask.FromResult<JsonObject?>(new JsonObject
        {
            ["source"] = FileState.Snapshot(source),
            ["destination"] = FileState.Snapshot(destination),
        });
    }

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var source = workspace.Resolve(context.GetString("source"));
        var destination = workspace.Resolve(context.GetString("destination"));
        var overwrite = context.GetBoolean("overwrite");
        if (TransferChecks.Validate(workspace, source, destination, overwrite) is { } failure)
        {
            return ValueTask.FromResult<NodeResult>(failure);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(source, destination, overwrite);
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            FileNodes.Output(("source", workspace.ToRelative(source)), ("destination", workspace.ToRelative(destination)))));
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var source = workspace.Resolve(context.GetString("source"));
        var destination = workspace.Resolve(context.GetString("destination"));
        var sourceBefore = context.Checkpoint?["source"] as JsonObject;
        var destinationBefore = context.Checkpoint?["destination"] as JsonObject;
        var sourceExists = File.Exists(source);
        var destinationHash = File.Exists(destination) ? FileState.Sha256(destination) : null;
        var relative = workspace.ToRelative(destination);

        if (!sourceExists && destinationHash is not null && destinationHash == FileState.Hash(sourceBefore))
        {
            return ValueTask.FromResult(VerificationResult.Done($"файл перемещён в «{relative}»."));
        }

        var destinationUnchanged = FileState.Exists(destinationBefore)
            ? destinationHash == FileState.Hash(destinationBefore)
            : destinationHash is null;
        if (sourceExists && FileState.Sha256(source) == FileState.Hash(sourceBefore) && destinationUnchanged)
        {
            return ValueTask.FromResult(VerificationResult.NotDone("файл остался на прежнем месте."));
        }

        return ValueTask.FromResult(VerificationResult.Inconclusive("файлы изменились не так, как ожидалось при перемещении."));
    }
}

internal static class TransferChecks
{
    public static NodeResult? Validate(IWorkspace workspace, string source, string destination, bool overwrite)
    {
        if (!File.Exists(source))
        {
            return NodeResult.Fail($"Исходный файл «{workspace.ToRelative(source)}» не найден.");
        }

        if (string.Equals(source, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return NodeResult.Fail("Исходный файл и файл назначения совпадают.", retryable: false);
        }

        if (Directory.Exists(destination))
        {
            return NodeResult.Fail($"По пути «{workspace.ToRelative(destination)}» находится папка. Укажите имя файла.", retryable: false);
        }

        if (File.Exists(destination) && !overwrite)
        {
            return NodeResult.Fail($"Файл «{workspace.ToRelative(destination)}» уже существует. " +
                                   "Включите замену или выберите другое имя.", retryable: false);
        }

        return null;
    }
}

public sealed class DeleteFileNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.delete";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Удалить файл",
        Description = "Удаляет файл в рабочей папке (без корзины). Папки не удаляет.",
        Icon = "trash-2",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields =
        [
            FileNodes.PathField(),
            new FieldDescriptor { Name = "missingOk", Label = "Не считать ошибкой, если файла нет", Kind = FieldKind.Boolean, Default = false },
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<JsonObject?>(new JsonObject
        {
            ["target"] = FileState.Snapshot(workspace.Resolve(context.GetString("path"))),
        });

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        var relative = workspace.ToRelative(path);
        if (Directory.Exists(path))
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail($"«{relative}» — это папка. Удаление папок не поддерживается.", retryable: false));
        }

        if (!File.Exists(path))
        {
            return ValueTask.FromResult<NodeResult>(context.GetBoolean("missingOk")
                ? NodeResult.Ok(Ports.Out, $"Файла «{relative}» не было — удалять нечего.")
                : NodeResult.Fail($"Файл «{relative}» не найден."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            FileNodes.Output(("path", relative))));
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        var before = context.Checkpoint?["target"] as JsonObject;
        var relative = workspace.ToRelative(path);
        if (!File.Exists(path))
        {
            return ValueTask.FromResult(VerificationResult.Done($"файла «{relative}» нет."));
        }

        return ValueTask.FromResult(FileState.Sha256(path) == FileState.Hash(before)
            ? VerificationResult.NotDone($"файл «{relative}» на месте.")
            : VerificationResult.Inconclusive($"файл «{relative}» существует, но изменился."));
    }
}

public sealed class CreateFolderNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "folder.create";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Создать папку",
        Description = "Создаёт папку (и недостающие папки по пути). Если папка уже есть — ничего не делает.",
        Icon = "folder-plus",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields = [FileNodes.PathField(label: "Папка", placeholder: "отчёты/2026")],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        if (File.Exists(path))
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail($"По пути «{workspace.ToRelative(path)}» уже есть файл.", retryable: false));
        }

        workspace.EnsureRootExists();
        Directory.CreateDirectory(path);
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?>(),
            FileNodes.Output(("path", workspace.ToRelative(path)))));
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        return ValueTask.FromResult(Directory.Exists(path)
            ? VerificationResult.Done("папка существует.")
            : VerificationResult.NotDone("папки нет."));
    }
}

public sealed class PathExistsNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.exists";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Проверить наличие",
        Description = "Проверяет, есть ли файл или папка, и направляет процесс по ветке «Есть» или «Нет».",
        Icon = "file-search",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "path",
        Outputs = [new PortDescriptor(Ports.True, "Есть"), new PortDescriptor(Ports.False, "Нет")],
        Fields =
        [
            FileNodes.PathField(label: "Путь"),
            new FieldDescriptor
            {
                Name = "kind", Label = "Что искать", Kind = FieldKind.Select, Default = "any",
                Options = [new("any", "Файл или папку"), new("file", "Только файл"), new("folder", "Только папку")],
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить результат в переменную", Kind = FieldKind.Variable },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = workspace.Resolve(context.GetString("path"));
        var exists = context.GetString("kind") switch
        {
            "file" => File.Exists(path),
            "folder" => Directory.Exists(path),
            _ => File.Exists(path) || Directory.Exists(path),
        };
        var updates = new Dictionary<string, JsonNode?>();
        if (context.GetString("saveTo") is { Length: > 0 } saveTo)
        {
            updates[saveTo] = exists;
        }

        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(exists ? Ports.True : Ports.False, updates,
            FileNodes.Output(("path", workspace.ToRelative(path)), ("exists", exists))));
    }
}

public sealed class ListFilesNode(IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "file.list";
    private const int MaxEntries = 10_000;

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Files.Id,
        Title = "Список файлов",
        Description = "Сохраняет в переменную список файлов папки (пути относительно рабочей папки).",
        Icon = "list-tree",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "folder",
        Outputs = [FileNodes.Next, FileNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "folder", Label = "Папка", Kind = FieldKind.Path, Placeholder = "пусто — вся рабочая папка",
                Help = "Путь внутри рабочей папки. Пусто — сама рабочая папка.",
            },
            new FieldDescriptor { Name = "pattern", Label = "Маска", Kind = FieldKind.Text, Default = "*", Placeholder = "*.txt" },
            new FieldDescriptor { Name = "recursive", Label = "Включая вложенные папки", Kind = FieldKind.Boolean, Default = false },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить в переменную", Kind = FieldKind.Variable, Required = true },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var folder = workspace.Resolve(context.GetString("folder"), allowRoot: true);
        if (!Directory.Exists(folder))
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail($"Папка «{workspace.ToRelative(folder)}» не найдена."));
        }

        var pattern = string.IsNullOrWhiteSpace(context.GetString("pattern")) ? "*" : context.GetString("pattern")!;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = context.GetBoolean("recursive"),
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var files = Directory.EnumerateFiles(folder, pattern, options)
            .Where(f => !Path.GetFileName(f).StartsWith(FileState.TempMarker, StringComparison.Ordinal))
            .Take(MaxEntries + 1)
            .Select(workspace.ToRelative)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count > MaxEntries)
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail($"Слишком много файлов (больше {MaxEntries}). Уточните маску.", retryable: false));
        }

        var list = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out,
            new Dictionary<string, JsonNode?> { [context.RequireString("saveTo")] = list },
            FileNodes.Output(("folder", workspace.ToRelative(folder)), ("count", files.Count))));
    }
}
