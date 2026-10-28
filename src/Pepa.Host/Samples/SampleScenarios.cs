using System.Text.Json.Nodes;
using Pepa.Core.Model;

namespace Pepa.Host.Samples;

/// <summary>Примеры, которые создаются при первом запуске: показывают основные возможности и сразу работают.</summary>
public static class SampleScenarios
{
    public static IReadOnlyList<ScenarioDefinition> All() => [.. Versioned().Select(s => s.Scenario)];

    /// <summary>Текущая версия набора примеров: при обновлении Pepa добавляются только новые.</summary>
    public const int CurrentVersion = 9;

    /// <summary>Примеры с номером версии набора, в которой они появились.</summary>
    public static IReadOnlyList<(int Version, ScenarioDefinition Scenario)> Versioned() =>
    [
        (1, Hello()), (1, Loop()), (1, WaitAndErrors()), (1, SortFolder()),
        (2, WaitForReply()), (2, GreetEveryone()),
        (3, BrowserPage()), (3, AdsPowerFlow()),
        (4, TelegramReply()), (4, AdsPowerTelegram()),
        (5, Parallel()),
        (6, Notepad()),
        (7, ExchangeRate()), (7, AskAndWrite()),
        (8, RunLog()),
        (9, MailCode()),
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

    public static ScenarioDefinition TelegramReply() => new()
    {
        Name = "Пример 9. Telegram: спросить и дождаться ответа",
        Description = "Отправляет сообщение в чат Telegram и ждёт ответ из этого же чата до 30 минут. Ожидание переживает закрытие Pepa. " +
                      "Нужен подключённый Telegram («Интеграции»).",
        Variables =
        [
            new VariableDefinition
            {
                Name = "chat", Type = VariableType.Text, Default = "", IsInput = true,
                Description = "Кому написать: @username или номер телефона из контактов",
            },
            new VariableDefinition { Name = "question", Type = VariableType.Text, Default = "Можно продолжать? Ответьте «да».", IsInput = true, Description = "Текст вопроса" },
            new VariableDefinition { Name = "reply", Type = VariableType.Any, Description = "Полученный ответ" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("send", "telegram.send", 260, 120, new JsonObject { ["chat"] = "{{ vars.chat }}", ["text"] = "{{ vars.question }}" }, "Спросить"),
            N("wait", "telegram.wait", 540, 120, new JsonObject
            {
                ["chat"] = "{{ vars.chat }}",
                ["timeoutSeconds"] = 1800,
                ["saveTo"] = "reply",
            }, "Ждать ответ"),
            N("done", "end", 820, 40, new JsonObject { ["outcome"] = "success", ["message"] = "Ответ: {{ vars.reply.payload.text }}" }),
            N("late", "end", 820, 220, new JsonObject { ["outcome"] = "failure", ["message"] = "Ответ не пришёл за 30 минут." }, "Ответа нет"),
        ],
        Edges = [E("start", "send"), E("send", "wait"), E("wait", "done", Ports.Event), E("wait", "late", Ports.Timeout)],
    };

    public static ScenarioDefinition AdsPowerTelegram() => new()
    {
        Name = "Пример 10. AdsPower + Telegram: сайт, сообщение и ответ",
        Description = "Открывает профиль AdsPower и сайт, делает снимок, пишет в Telegram и ждёт ответ со словом «готово» до 30 минут; " +
                      "затем закрывает профиль. Профиль занят этим процессом, ответ из чата получает только он. Нужны AdsPower и Telegram.",
        Variables =
        [
            new VariableDefinition { Name = "profile", Type = VariableType.Text, Default = "", IsInput = true, Description = "ID профиля AdsPower" },
            new VariableDefinition { Name = "url", Type = VariableType.Text, Default = "https://example.com", IsInput = true, Description = "Адрес сайта" },
            new VariableDefinition
            {
                Name = "chat", Type = VariableType.Text, Default = "", IsInput = true,
                Description = "Кому написать: @username или номер телефона из контактов",
            },
            new VariableDefinition { Name = "title", Type = VariableType.Text, Description = "Заголовок страницы" },
            new VariableDefinition { Name = "reply", Type = VariableType.Any, Description = "Полученный ответ" },
        ],
        Nodes =
        [
            N("start", "start", 0, 120),
            N("open", "adspower.open", 260, 120, new JsonObject { ["profileId"] = "{{ vars.profile }}", ["url"] = "{{ vars.url }}", ["closeOnFinish"] = true },
                "Открыть профиль"),
            N("read", "browser.read", 540, 120, new JsonObject { ["what"] = "title", ["saveTo"] = "title" }, "Заголовок"),
            N("shot", "browser.screenshot", 820, 120, new JsonObject { ["path"] = "примеры/adspower/{{ vars.profile }}.png" }, "Снимок"),
            N("send", "telegram.send", 1100, 120, new JsonObject
            {
                ["chat"] = "{{ vars.chat }}",
                ["text"] = "Профиль {{ vars.profile }}: открыта страница «{{ vars.title }}». Напишите «готово», когда можно закрывать.",
            }, "Написать"),
            N("wait", "telegram.wait", 1380, 120, new JsonObject
            {
                ["chat"] = "{{ vars.chat }}",
                ["contains"] = "готово",
                ["timeoutSeconds"] = 1800,
                ["saveTo"] = "reply",
            }, "Ждать «готово»"),
            N("close", "browser.close", 1660, 40, new JsonObject { ["stopProfile"] = true }, "Закрыть профиль"),
            N("done", "end", 1940, 40, new JsonObject { ["outcome"] = "success", ["message"] = "Ответ: {{ vars.reply.payload.text }}" }),
            N("late", "end", 1660, 260, new JsonObject { ["outcome"] = "failure", ["message"] = "Ответ не пришёл за 30 минут. Профиль закроется сам." }, "Ответа нет"),
        ],
        Edges =
        [
            E("start", "open"), E("open", "read"), E("read", "shot"), E("shot", "send"), E("send", "wait"),
            E("wait", "close", Ports.Event), E("wait", "late", Ports.Timeout), E("close", "done"),
        ],
    };

    public static ScenarioDefinition Parallel() => new()
    {
        Name = "Пример 11. Параллельно: ждать ответ и работать",
        Description = "Делит процесс на две ветви: одна ждёт ответ-событие с ключом заказа (до 10 минут), другая тем временем " +
                      "записывает файл и пишет в журнал. «Дождаться всех ветвей» продолжает процесс, когда обе закончили. " +
                      "Отправьте ответ на странице «Выполнение» → «Отправить событие» с ключом заказа.",
        Variables =
        [
            new VariableDefinition { Name = "order", Type = VariableType.Text, Default = "A-200", IsInput = true, Description = "Номер заказа (ключ ответа)" },
            new VariableDefinition { Name = "reply", Type = VariableType.Any, Description = "Полученный ответ" },
        ],
        Nodes =
        [
            N("start", "start", 0, 140),
            N("split", "parallel", 260, 140, label: "Две ветви"),
            N("wait", "event.wait", 540, 40, new JsonObject
            {
                ["source"] = "manual",
                ["correlationKey"] = "{{ vars.order }}",
                ["timeoutSeconds"] = 600,
                ["saveTo"] = "reply",
            }, "Ждать ответ"),
            N("write", "file.write", 540, 260, new JsonObject
            {
                ["path"] = "примеры/параллельно/{{ vars.order }}.txt",
                ["content"] = "Заказ {{ vars.order }}: работа шла, пока ждали ответ.",
                ["mode"] = "overwrite",
            }, "Записать файл"),
            N("log", "log", 820, 260, new JsonObject { ["message"] = "Файл записан, ждём вторую ветвь." }),
            N("join", "join", 1100, 140, label: "Обе ветви готовы"),
            N("done", "end", 1380, 140, new JsonObject { ["outcome"] = "success", ["message"] = "Ответ: {{ vars.reply.payload }}" }),
            N("late", "end", 820, 40, new JsonObject { ["outcome"] = "failure", ["message"] = "Ответ не пришёл за 10 минут." }, "Ответа нет"),
        ],
        Edges =
        [
            E("start", "split"), E("split", "wait", "b1"), E("split", "write", "b2"), E("write", "log"), E("log", "join"),
            E("wait", "join", Ports.Event), E("wait", "late", Ports.Timeout), E("join", "done"),
        ],
    };

    public static ScenarioDefinition Notepad() => new()
    {
        Name = "Пример 12. Окна Windows: напечатать в Блокноте",
        Description = "Запускает Блокнот, дожидается его окна и печатает текст. Работает только в Pepa.exe на Windows. Перед запуском " +
                      "закройте другие окна Блокнота: если подходит несколько окон, Pepa остановится и спросит, а не напечатает наугад. " +
                      "Дальше можно добавить «Ввести в окне» или «Прочитать из окна» — поле Блокнота выберите кнопкой «Выбрать элемент в окне». " +
                      "В Windows на английском замените «Блокнот» на «Notepad» в обоих блоках.",
        Variables =
        [
            new VariableDefinition { Name = "text", Type = VariableType.Text, Default = "Привет от Pepa!", IsInput = true, Description = "Что напечатать" },
        ],
        Nodes =
        [
            N("start", "start", 0, 100),
            N("run", "desktop.start", 260, 100, new JsonObject { ["path"] = "notepad", ["window"] = "Блокнот", ["waitSeconds"] = 30 },
                "Запустить Блокнот"),
            N("type", "desktop.keys", 540, 100, new JsonObject
            {
                ["window"] = "Блокнот", ["process"] = "notepad", ["mode"] = "type", ["text"] = "{{ vars.text }}", ["waitSeconds"] = 10,
            }, "Напечатать текст"),
            N("done", "end", 820, 100, new JsonObject
            {
                ["outcome"] = "success",
                ["message"] = "Текст напечатан. Блокнот остаётся открытым — закройте его сами (сохранять не обязательно).",
            }),
            N("noWindow", "end", 540, 260, new JsonObject
            {
                ["outcome"] = "failure",
                ["message"] = "Окно Блокнота не появилось за 30 секунд — ничего не напечатано.",
            }, "Окна нет"),
        ],
        Edges = [E("start", "run"), E("run", "type"), E("run", "noWindow", Ports.Timeout), E("type", "done")],
    };

    public static ScenarioDefinition ExchangeRate() => new()
    {
        Name = "Пример 13. Курс доллара из API",
        Description = "Получает курсы валют по адресу API (open.er-api.com, без ключа) и записывает курс доллара в рублях в файл. " +
                      "Нужен интернет. Если сервис не ответит, процесс пойдёт по выходу «Ошибка» и закончится с понятной причиной.",
        Variables =
        [
            new VariableDefinition { Name = "rates", Type = VariableType.Any, Description = "Ответ API" },
        ],
        Nodes =
        [
            N("start", "start", 0, 100),
            N("get", "http.get", 260, 100, new JsonObject { ["url"] = "https://open.er-api.com/v6/latest/USD", ["saveTo"] = "rates" },
                "Получить курсы", new NodeSettings { RetryCount = 2, RetryDelaySeconds = 10 }),
            N("write", "file.write", 540, 100, new JsonObject
            {
                ["path"] = "примеры/курс доллара.txt",
                ["content"] = "1 USD = {{ vars.rates.rates.RUB }} RUB (обновлено {{ vars.rates.time_last_update_utc }})",
            }, "Записать курс"),
            N("done", "end", 820, 100, new JsonObject { ["outcome"] = "success", ["message"] = "1 USD = {{ vars.rates.rates.RUB }} RUB" }),
            N("failed", "end", 540, 260, new JsonObject { ["outcome"] = "failure", ["message"] = "Курс не получен — смотрите ошибку шага «Получить курсы»." },
                "Нет ответа"),
        ],
        Edges = [E("start", "get"), E("get", "write"), E("get", "failed", Ports.Error), E("write", "done")],
    };

    public static ScenarioDefinition AskAndWrite() => new()
    {
        Name = "Пример 14. Спросить и записать",
        Description = "Спрашивает, создать ли отчёт, и как его назвать — вопросы появятся на «Главной» (и придут уведомлением, если оно " +
                      "включено). Без ответа за час процесс закончится сам и ничего не создаст.",
        Variables =
        [
            new VariableDefinition { Name = "create", Type = VariableType.Text, Description = "Создать отчёт?" },
            new VariableDefinition { Name = "name", Type = VariableType.Text, Description = "Название отчёта" },
        ],
        Nodes =
        [
            N("start", "start", 0, 100),
            N("confirm", "user.ask", 260, 100, new JsonObject
            {
                ["question"] = "Создать отчёт?", ["options"] = "Создать\nНе нужно",
                ["timeoutSeconds"] = 3600, ["saveTo"] = "create",
            }, "Создать отчёт?"),
            N("check", "condition", 540, 100, new JsonObject { ["expression"] = "vars.create === 'Создать'" }, "Создать?"),
            N("name", "user.ask", 820, 40, new JsonObject
            {
                ["question"] = "Как назвать отчёт?", ["timeoutSeconds"] = 3600, ["saveTo"] = "name",
            }, "Название"),
            N("write", "file.write", 1100, 40, new JsonObject
            {
                ["path"] = "примеры/отчёты/{{ vars.name }}.txt",
                ["content"] = "Отчёт «{{ vars.name }}» создан процессом {{ process.id }}.",
                ["mode"] = "createNew",
            }, "Записать отчёт"),
            N("done", "end", 1380, 40, new JsonObject { ["outcome"] = "success", ["message"] = "Отчёт «{{ vars.name }}» создан." }),
            N("skip", "end", 820, 220, new JsonObject { ["outcome"] = "success", ["message"] = "Отчёт не нужен — ничего не создано." }, "Не нужно"),
            N("late", "end", 540, 300, new JsonObject { ["outcome"] = "failure", ["message"] = "Ответа не было — ничего не создано." }, "Нет ответа"),
        ],
        Edges =
        [
            E("start", "confirm"), E("confirm", "check", Ports.Event), E("confirm", "late", Ports.Timeout),
            E("check", "name", Ports.True), E("check", "skip", Ports.False),
            E("name", "write", Ports.Event), E("name", "late", Ports.Timeout), E("write", "done"),
        ],
    };

    public static ScenarioDefinition RunLog() => new()
    {
        Name = "Пример 15. Таблица: журнал запусков",
        Description = "Дописывает строку в таблицу Excel «примеры/журнал запусков.xlsx» (при первом запуске создаёт её) и читает, " +
                      "сколько в ней строк. Таблица, открытая в Excel, не меняется — процесс остановится и попросит её закрыть.",
        Variables =
        [
            new VariableDefinition { Name = "name", Type = VariableType.Text, Default = "мир", IsInput = true, Description = "Кто запустил" },
            new VariableDefinition { Name = "rows", Type = VariableType.List, Description = "Строки журнала" },
        ],
        Nodes =
        [
            N("start", "start", 0, 100),
            N("append", "table.append", 260, 100, new JsonObject
            {
                ["path"] = "примеры/журнал запусков.xlsx",
                ["values"] = "({ 'Запущен (UTC)': process.startedAt.slice(0, 19).replace('T', ' '), Кто: vars.name, Процесс: process.id })",
            }, "Добавить строку"),
            N("read", "table.read", 540, 100, new JsonObject { ["path"] = "примеры/журнал запусков.xlsx", ["saveTo"] = "rows" }, "Прочитать журнал"),
            N("done", "end", 820, 100, new JsonObject { ["outcome"] = "success", ["message"] = "В журнале строк: {{ vars.rows.length }}." }),
            N("failed", "end", 540, 260, new JsonObject { ["outcome"] = "failure", ["message"] = "Журнал не записан: {{ lastError.message }}" },
                "Не записано"),
        ],
        Edges = [E("start", "append"), E("append", "read"), E("append", "failed", Ports.Error), E("read", "done")],
    };

    public static ScenarioDefinition MailCode() => new()
    {
        Name = "Пример 16. Почта: код из письма",
        Description = "Ждёт новое письмо с указанными словами в теме (до 10 минут) и достаёт из него код из цифр — как после " +
                      "регистрации на сайте. Нужна подключённая почта: «Интеграции» → «Почта». Запустите пример и отправьте себе письмо " +
                      "с темой «Код» и цифрами в тексте.",
        Variables =
        [
            new VariableDefinition
            {
                Name = "subject", Type = VariableType.Text, Default = "код", IsInput = true, Description = "Тема письма содержит",
            },
            new VariableDefinition { Name = "mail", Type = VariableType.Any, Description = "Полученное письмо" },
            new VariableDefinition { Name = "code", Type = VariableType.Text, Description = "Код из письма" },
        ],
        Nodes =
        [
            N("start", "start", 0, 100),
            N("wait", "email.wait", 260, 100, new JsonObject
            {
                ["subject"] = "{{ vars.subject }}", ["timeoutSeconds"] = 600, ["lookbackSeconds"] = 0, ["saveTo"] = "mail",
            }, "Ждать письмо"),
            N("find", "text.find", 540, 100, new JsonObject
            {
                ["text"] = "{{ vars.mail.payload.subject }}\n{{ vars.mail.payload.text }}", ["what"] = "code", ["saveTo"] = "code",
            }, "Найти код"),
            N("done", "end", 820, 40, new JsonObject { ["outcome"] = "success", ["message"] = "Код из письма «{{ vars.mail.payload.subject }}»: {{ vars.code }}" }),
            N("noCode", "end", 820, 200, new JsonObject { ["outcome"] = "failure", ["message"] = "В письме «{{ vars.mail.payload.subject }}» нет кода из цифр." },
                "Кода нет"),
            N("late", "end", 540, 300, new JsonObject { ["outcome"] = "failure", ["message"] = "За 10 минут письма не было." }, "Нет письма"),
        ],
        Edges =
        [
            E("start", "wait"), E("wait", "find", Ports.Event), E("wait", "late", Ports.Timeout),
            E("find", "done", Ports.True), E("find", "noCode", Ports.False),
        ],
    };
}
