using System.Text.Json.Nodes;
using Pepa.Core.Model;

namespace Pepa.Host.Samples;

/// <summary>Примеры, которые создаются при первом запуске: показывают основные возможности и сразу работают.</summary>
public static class SampleScenarios
{
    public static IReadOnlyList<ScenarioDefinition> All() => [Hello(), Loop(), WaitAndErrors(), SortFolder()];

    private static NodeDefinition N(string id, string type, double x, double y, JsonObject? config = null,
        string? label = null, NodeSettings? settings = null) => new()
    {
        Id = id,
        Type = type,
        Label = label,
        Position = new NodePosition(x, y),
        Config = config ?? [],
        Settings = settings ?? new NodeSettings(),
    };

    private static EdgeDefinition E(string source, string target, string port = Ports.Out) => new()
    {
        Id = $"{source}-{port}-{target}",
        Source = source,
        SourcePort = port,
        Target = target,
    };

    public static ScenarioDefinition Hello() => new()
    {
        Name = "Пример 1. Привет, файл",
        Description = "Записывает файл в рабочую папку, читает его обратно и пишет содержимое в журнал.",
        Variables =
        [
            new VariableDefinition { Name = "name", Type = VariableType.Text, Default = "мир", IsInput = true, Description = "Кого приветствовать" },
            new VariableDefinition { Name = "text", Type = VariableType.Text, Description = "Прочитанный текст" },
        ],
        Nodes =
        [
            N("start", "start", 0, 80),
            N("write", "file.write", 260, 80, new JsonObject
            {
                ["path"] = "примеры/привет.txt",
                ["content"] = "Привет, {{ vars.name }}! Файл создан процессом {{ process.id }}.",
                ["mode"] = "overwrite",
            }, "Записать приветствие"),
            N("read", "file.read", 540, 80, new JsonObject { ["path"] = "примеры/привет.txt", ["saveTo"] = "text" }, "Прочитать обратно"),
            N("log", "log", 820, 80, new JsonObject { ["message"] = "В файле: {{ vars.text }}", ["level"] = "info" }),
            N("end", "end", 1100, 80, new JsonObject { ["outcome"] = "success", ["message"] = "Готово: примеры/привет.txt" }),
        ],
        Edges = [E("start", "write"), E("write", "read"), E("read", "log"), E("log", "end")],
    };

    public static ScenarioDefinition Loop() => new()
    {
        Name = "Пример 2. Цикл: несколько файлов",
        Description = "Создаёт заданное количество файлов: переменная-счётчик, условие и возврат по кругу.",
        Variables =
        [
            new VariableDefinition { Name = "count", Type = VariableType.Number, Default = 3, IsInput = true, Description = "Сколько файлов создать" },
            new VariableDefinition { Name = "i", Type = VariableType.Number, Default = 1, Description = "Номер текущего файла" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("folder", "folder.create", 260, 120, new JsonObject { ["path"] = "примеры/цикл" }, "Папка для файлов"),
            N("check", "condition", 540, 120, new JsonObject { ["expression"] = "vars.i <= vars.count" }, "Ещё есть файлы?"),
            N("write", "file.write", 820, 0, new JsonObject
            {
                ["path"] = "примеры/цикл/файл-{{ vars.i }}.txt",
                ["content"] = "Это файл номер {{ vars.i }} из {{ vars.count }}.",
                ["mode"] = "overwrite",
            }, "Записать файл"),
            N("next", "variable.set", 1100, 0, new JsonObject { ["variable"] = "i", ["value"] = "vars.i + 1" }, "Следующий номер"),
            N("log", "log", 820, 260, new JsonObject { ["message"] = "Создано файлов: {{ vars.count }}", ["level"] = "info" }),
            N("end", "end", 1100, 260, new JsonObject { ["outcome"] = "success" }),
        ],
        Edges =
        [
            E("start", "folder"), E("folder", "check"), E("check", "write", Ports.True), E("check", "log", Ports.False),
            E("write", "next"), E("next", "check"), E("log", "end"),
        ],
    };

    public static ScenarioDefinition WaitAndErrors() => new()
    {
        Name = "Пример 3. Ожидание и обработка ошибки",
        Description = "Ждёт 15 секунд (можно закрыть приложение — ожидание сохранится), затем читает заявку. " +
                      "Если файла нет — ветка «Ошибка» создаёт его.",
        Variables = [new VariableDefinition { Name = "text", Type = VariableType.Text, Description = "Текст заявки" }],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("hello", "log", 260, 120, new JsonObject { ["message"] = "Ждём 15 секунд перед чтением заявки", ["level"] = "info" }),
            N("wait", "wait", 540, 120, new JsonObject { ["mode"] = "duration", ["amount"] = 15, ["unit"] = "seconds" }),
            N("read", "file.read", 820, 120, new JsonObject { ["path"] = "примеры/входящие/заявка.txt", ["saveTo"] = "text" },
                "Прочитать заявку", new NodeSettings { RetryCount = 1, RetryDelaySeconds = 3 }),
            N("ok", "log", 1100, 0, new JsonObject { ["message"] = "Заявка: {{ vars.text }}", ["level"] = "info" }),
            N("done", "end", 1380, 0, new JsonObject { ["outcome"] = "success", ["message"] = "Заявка прочитана" }),
            N("warn", "log", 1100, 260, new JsonObject
            {
                ["message"] = "Заявки не было ({{ lastError.message }}). Создаю её.",
                ["level"] = "warning",
            }),
            N("create", "file.write", 1380, 260, new JsonObject
            {
                ["path"] = "примеры/входящие/заявка.txt",
                ["content"] = "Заявка создана автоматически. Запустите пример ещё раз — теперь файл найдётся.",
                ["mode"] = "createNew",
            }, "Создать заявку"),
            N("created", "end", 1660, 260, new JsonObject { ["outcome"] = "success", ["message"] = "Заявка создана" }),
        ],
        Edges =
        [
            E("start", "hello"), E("hello", "wait"), E("wait", "read"), E("read", "ok"), E("read", "warn", Ports.Error),
            E("ok", "done"), E("warn", "create"), E("create", "created"),
        ],
    };

    public static ScenarioDefinition SortFolder() => new()
    {
        Name = "Пример 4. Разобрать папку «входящие»",
        Description = "Берёт все .txt из папки «примеры/входящие» и перемещает их в «примеры/обработано», по одному.",
        Variables =
        [
            new VariableDefinition { Name = "files", Type = VariableType.List, Default = new JsonArray(), Description = "Найденные файлы" },
            new VariableDefinition { Name = "i", Type = VariableType.Number, Default = 0, Description = "Номер текущего файла" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("ensure", "folder.create", 260, 120, new JsonObject { ["path"] = "примеры/входящие" }, "Папка «входящие»"),
            N("list", "file.list", 540, 120, new JsonObject
            {
                ["folder"] = "примеры/входящие",
                ["pattern"] = "*.txt",
                ["recursive"] = false,
                ["saveTo"] = "files",
            }, "Найти файлы"),
            N("check", "condition", 820, 120, new JsonObject { ["expression"] = "vars.i < vars.files.length" }, "Остались файлы?"),
            N("move", "file.move", 1100, 0, new JsonObject
            {
                ["source"] = "{{ vars.files[vars.i] }}",
                ["destination"] = "примеры/обработано/{{ vars.files[vars.i].split('/').pop() }}",
                ["overwrite"] = false,
            }, "Переместить файл"),
            N("next", "variable.set", 1380, 0, new JsonObject { ["variable"] = "i", ["value"] = "vars.i + 1" }, "Следующий"),
            N("end", "end", 1100, 260, new JsonObject { ["outcome"] = "success", ["message"] = "Перемещено файлов: {{ vars.files.length }}" }),
        ],
        Edges =
        [
            E("start", "ensure"), E("ensure", "list"), E("list", "check"), E("check", "move", Ports.True),
            E("check", "end", Ports.False), E("move", "next"), E("next", "check"),
        ],
    };
}
