using System.Text.Json.Nodes;
using Pepa.Core.Model;

namespace Pepa.Host.Samples;

/// <summary>Примеры, которые создаются при первом запуске: показывают основные возможности и сразу работают.</summary>
public static class SampleScenarios
{
    public static IReadOnlyList<ScenarioDefinition> All() => [.. Versioned().Select(s => s.Scenario)];

    /// <summary>Текущая версия набора примеров: при обновлении Pepa добавляются только новые.</summary>
    public const int CurrentVersion = 3;

    /// <summary>Примеры с номером версии набора, в которой они появились.</summary>
    public static IReadOnlyList<(int Version, ScenarioDefinition Scenario)> Versioned() =>
    [
        (1, Hello()), (1, Loop()), (1, WaitAndErrors()), (1, SortFolder()),
        (2, WaitForReply()), (2, GreetEveryone()),
        (3, BrowserPage()), (3, AdsPowerFlow()),
    ];

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

    public static ScenarioDefinition WaitForReply() => new()
    {
        Name = "Пример 5. Занять профиль и ждать ответ",
        Description = "Занимает ресурс «профиль», ждёт событие-ответ по номеру заказа (до 10 минут) и сохраняет его в файл. " +
                      "Отправьте событие вручную на странице «Выполнение»: источник manual, вид reply, ключ — номер заказа.",
        Variables =
        [
            new VariableDefinition { Name = "order", Type = VariableType.Text, Default = "A-100", IsInput = true, Description = "Номер заказа" },
            new VariableDefinition { Name = "reply", Type = VariableType.Any, Description = "Полученное событие" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("lock", "resource.acquire", 260, 120, new JsonObject
            {
                ["key"] = "пример:профиль-1",
                ["title"] = "Профиль браузера (пример)",
                ["timeoutSeconds"] = 300,
            }, "Занять профиль"),
            N("hint", "log", 540, 120, new JsonObject
            {
                ["message"] = "Профиль занят. Жду ответ по заказу {{ vars.order }}: отправьте событие manual / reply с ключом {{ vars.order }}.",
                ["level"] = "info",
            }, "Подсказка"),
            N("wait", "event.wait", 820, 120, new JsonObject
            {
                ["source"] = "manual",
                ["eventType"] = "reply",
                ["correlationKey"] = "{{ vars.order }}",
                ["timeoutSeconds"] = 600,
                ["saveTo"] = "reply",
            }, "Ждать ответ"),
            N("save", "file.write", 1100, 0, new JsonObject
            {
                ["path"] = "примеры/ответы/{{ vars.order }}.txt",
                ["content"] = "Ответ по заказу {{ vars.order }}: {{ vars.reply.payload }}",
                ["mode"] = "overwrite",
            }, "Сохранить ответ"),
            N("free", "resource.release", 1380, 0, new JsonObject { ["key"] = "пример:профиль-1" }, "Освободить профиль"),
            N("done", "end", 1660, 0, new JsonObject { ["outcome"] = "success", ["message"] = "Ответ сохранён: примеры/ответы/{{ vars.order }}.txt" }),
            N("late", "end", 1100, 260, new JsonObject { ["outcome"] = "failure", ["message"] = "Ответ по заказу {{ vars.order }} не пришёл за 10 минут." }, "Ответа нет"),
            N("busy", "end", 540, 300, new JsonObject { ["outcome"] = "failure", ["message"] = "Профиль 5 минут был занят другим процессом." }, "Профиль занят"),
        ],
        Edges =
        [
            E("start", "lock"), E("lock", "hint"), E("lock", "busy", Ports.Timeout), E("hint", "wait"),
            E("wait", "save", Ports.Event), E("wait", "late", Ports.Timeout), E("save", "free"), E("free", "done"),
        ],
    };

    public static ScenarioDefinition GreetEveryone() => new()
    {
        Name = "Пример 6. Для каждого из списка",
        Description = "Перебирает список имён и для каждого создаёт файл. Список можно задать при запуске.",
        Variables =
        [
            new VariableDefinition
            {
                Name = "names", Type = VariableType.List, Default = new JsonArray("Анна", "Борис", "Вера"), IsInput = true,
                Description = "Кого поприветствовать",
            },
            new VariableDefinition { Name = "name", Type = VariableType.Text, Description = "Текущее имя" },
            new VariableDefinition { Name = "index", Type = VariableType.Number, Default = 0, Description = "Номер имени (с 0)" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("each", "loop.forEach", 260, 120, new JsonObject
            {
                ["items"] = "vars.names",
                ["itemVariable"] = "name",
                ["indexVariable"] = "index",
            }, "Для каждого имени"),
            N("write", "file.write", 540, 0, new JsonObject
            {
                ["path"] = "примеры/список/{{ vars.index + 1 }}-{{ vars.name }}.txt",
                ["content"] = "Привет, {{ vars.name }}!",
                ["mode"] = "overwrite",
            }, "Записать файл"),
            N("end", "end", 540, 260, new JsonObject { ["outcome"] = "success", ["message"] = "Создано файлов: {{ vars.names.length }}" }),
        ],
        Edges = [E("start", "each"), E("each", "write", Ports.Item), E("write", "each"), E("each", "end", Ports.Done)],
    };

    public static ScenarioDefinition BrowserPage() => new()
    {
        Name = "Пример 7. Браузер: открыть страницу и сохранить заголовок",
        Description = "Открывает Microsoft Edge, переходит по адресу, читает заголовок страницы, делает снимок и закрывает браузер.",
        Variables =
        [
            new VariableDefinition { Name = "url", Type = VariableType.Text, Default = "https://example.com", IsInput = true, Description = "Адрес страницы" },
            new VariableDefinition { Name = "title", Type = VariableType.Text, Description = "Заголовок страницы" },
        ],
        Nodes =
        [
            N("start", "start", 0, 100),
            N("open", "browser.open", 260, 100, new JsonObject { ["browser"] = "msedge", ["url"] = "{{ vars.url }}", ["keepLogin"] = true, ["profile"] = "main" },
                "Открыть Edge"),
            N("title", "browser.read", 540, 100, new JsonObject { ["what"] = "title", ["saveTo"] = "title" }, "Прочитать заголовок"),
            N("shot", "browser.screenshot", 820, 100, new JsonObject { ["path"] = "примеры/снимки/{{ process.id }}.png" }, "Снимок страницы"),
            N("close", "browser.close", 1100, 100, new JsonObject(), "Закрыть браузер"),
            N("end", "end", 1380, 100, new JsonObject { ["outcome"] = "success", ["message"] = "Заголовок: {{ vars.title }}" }),
        ],
        Edges = [E("start", "open"), E("open", "title"), E("title", "shot"), E("shot", "close"), E("close", "end")],
    };

    public static ScenarioDefinition AdsPowerFlow() => new()
    {
        Name = "Пример 8. AdsPower: профиль, сайт и ожидание ответа",
        Description = "Открывает профиль AdsPower, переходит на сайт, делает снимок и ждёт ответ (событие) до 30 минут; затем закрывает профиль. " +
                      "Профиль занят этим процессом — другой процесс с тем же профилем подождёт. Нужен запущенный AdsPower.",
        Variables =
        [
            new VariableDefinition { Name = "profile", Type = VariableType.Text, Default = "", IsInput = true, Description = "ID профиля AdsPower" },
            new VariableDefinition { Name = "url", Type = VariableType.Text, Default = "https://example.com", IsInput = true, Description = "Адрес сайта" },
            new VariableDefinition { Name = "reply", Type = VariableType.Any, Description = "Полученный ответ" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("open", "adspower.open", 260, 120, new JsonObject { ["profileId"] = "{{ vars.profile }}", ["url"] = "{{ vars.url }}", ["closeOnFinish"] = true },
                "Открыть профиль"),
            N("shot", "browser.screenshot", 540, 120, new JsonObject { ["path"] = "примеры/adspower/{{ vars.profile }}.png" }, "Снимок"),
            N("hint", "log", 820, 120, new JsonObject
            {
                ["message"] = "Жду ответ: событие manual / reply с ключом {{ vars.profile }} (на странице «Выполнение» → «Отправить событие»).",
                ["level"] = "info",
            }, "Подсказка"),
            N("wait", "event.wait", 1100, 120, new JsonObject
            {
                ["source"] = "manual",
                ["eventType"] = "reply",
                ["correlationKey"] = "{{ vars.profile }}",
                ["timeoutSeconds"] = 1800,
                ["saveTo"] = "reply",
            }, "Ждать ответ"),
            N("close", "browser.close", 1380, 40, new JsonObject { ["stopProfile"] = true }, "Закрыть профиль"),
            N("done", "end", 1660, 40, new JsonObject { ["outcome"] = "success", ["message"] = "Ответ: {{ vars.reply.payload }}" }),
            N("late", "end", 1380, 260, new JsonObject { ["outcome"] = "failure", ["message"] = "Ответ не пришёл за 30 минут. Профиль закроется сам." }, "Ответа нет"),
        ],
        Edges =
        [
            E("start", "open"), E("open", "shot"), E("shot", "hint"), E("hint", "wait"),
            E("wait", "close", Ports.Event), E("wait", "late", Ports.Timeout), E("close", "done"),
        ],
    };
}
