using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Text;

namespace Pepa.Adapters.Browser;

/// <summary>
/// «Ждать одно из» (D-084): после «Войти» сайт показывает одно из нескольких — кабинет, капчу, «неверный пароль», запрос
/// кода. Все исходы проверяются одновременно, и процесс идёт по выходу того, что появилось; если не появилось ничего —
/// «Ничего из этого» (не подключён — остановка со снимком страницы). Два исхода сразу — тоже остановка: непонятно, что
/// случилось. Блок только читает страницу.
/// </summary>
public sealed partial class WaitAnyNode(BrowserSessions sessions) : INodeExecutor
{
    public const string TypeName = "browser.waitAny";
    public const int MaxOutcomes = 4;

    /// <summary>Как часто смотреть на страницу.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public static string NameField(int index) => $"name{index}";

    public static string KindField(int index) => $"kind{index}";

    public static string TargetField(int index) => $"target{index}";

    public static string OutcomePort(int index) => $"o{index}";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = BrowserNodes.Category.Id,
        Title = "Ждать одно из",
        Description = "Ждёт, что сайт покажет одно из нескольких (кабинет, капча, «неверный пароль»…), и ведёт процесс по выходу " +
                      "того, что появилось. Если не появилось ничего — «Ничего из этого»; не подключён — остановка со снимком. Ничего не меняет.",
        Icon = "scan-search",
        Effect = NodeEffect.ReadOnly,
        SummaryField = NameField(1),
        Outputs =
        [
            .. Enumerable.Range(1, MaxOutcomes).Select(i =>
                new PortDescriptor(OutcomePort(i), $"Исход {i}", PortKind.Optional) { LabelField = NameField(i) }),
            new PortDescriptor(Ports.Timeout, "Ничего из этого", PortKind.Optional),
            BrowserNodes.Error,
        ],
        Fields =
        [
            .. Enumerable.Range(1, MaxOutcomes).SelectMany(i => new[]
            {
                new FieldDescriptor
                {
                    Name = NameField(i), Label = $"Исход {i}: название", Kind = FieldKind.Text, Required = i <= 2,
                    Placeholder = i switch { 1 => "Кабинет", 2 => "Неверный пароль", 3 => "Капча", _ => "Запрос кода" },
                    Help = i == 1 ? "Так будет подписан выход. Пустое название — исхода нет." : null,
                },
                new FieldDescriptor
                {
                    Name = KindField(i), Label = $"Исход {i}: когда", Kind = FieldKind.Select, Default = "element",
                    Options = [new("element", "Появился элемент"), new("url", "Адрес содержит"), new("text", "На странице есть текст")],
                },
                new FieldDescriptor
                {
                    Name = TargetField(i), Label = $"Исход {i}: что", Kind = FieldKind.Template,
                    Placeholder = i switch { 1 => "#dashboard", 2 => ".error", 3 => "iframe[src*=captcha]", _ => "input[name=code]" },
                    Help = i == 1
                        ? "Элемент — селектор (#id, .класс, text=Выйти); адрес — часть адреса; текст — фраза (регистр и «ё» не важны). " +
                          "Указывайте то, что появляется после действия, а не то, что видно до него (иначе исход совпадёт сразу)."
                        : null,
                },
            }),
            new FieldDescriptor
            {
                Name = "waitSeconds", Label = "Ждать, секунд", Kind = FieldKind.Seconds, Default = 30, Min = 1, Max = 600,
                Help = "Сколько ждать, пока появится хоть один исход.",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить итог в", Kind = FieldKind.Variable,
                Help = "Необязательно. Объект: outcome (номер исхода, 0 — ничего), name (название) и url (адрес страницы).",
            },
            BrowserNodes.SessionField,
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => BrowserNodes.WaitSpan(input);

    private sealed record Outcome(int Index, string Name, string Kind, string Target);

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        BrowserNodes.Run(context, sessions, cancellationToken, async (session, _) =>
        {
            var outcomes = new List<Outcome>();
            for (var i = 1; i <= MaxOutcomes; i++)
            {
                var name = context.Node.Config.GetString(NameField(i))?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var target = context.GetString(TargetField(i))?.Trim();
                if (string.IsNullOrEmpty(target))
                {
                    throw new NodeInputException($"У исхода «{name}» не задано, что ждать (поле «Исход {i}: что»).");
                }

                outcomes.Add(new Outcome(i, name, context.GetString(KindField(i)) ?? "element", target));
            }

            if (outcomes.Count == 0)
            {
                throw new NodeInputException("Не задан ни один исход.");
            }

            var wait = BrowserNodes.WaitSpan(context.Input);
            var deadline = DateTimeOffset.UtcNow + wait;
            IReadOnlyList<Outcome>? previous = null;
            var glimpsed = new SortedDictionary<int, Outcome>();
            var lastLook = false;
            IPage page = session.CurrentPage();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    page = session.CurrentPage();
                }
                catch (BrowserSessionLostException lost)
                {
                    return NodeResult.Fail(lost.Message, retryable: false);
                }

                var seen = new List<Outcome>();
                try
                {
                    foreach (var outcome in outcomes)
                    {
                        if (await SeenAsync(page, outcome))
                        {
                            seen.Add(outcome);
                        }
                    }
                }
                catch (PlaywrightException) when (page.IsClosed && !session.Closed)
                {
                    // Вкладка закрылась во время проверки (окно входа закрылось само) — смотрим заново на ту, что стала текущей.
                    previous = null;
                    continue;
                }

                foreach (var outcome in seen)
                {
                    glimpsed[outcome.Index] = outcome;
                }

                // Одно и то же два раза подряд: страница в переходе (адрес сменился, элемент мелькнул) не решает исход.
                if (seen.Count > 0 && previous is not null && previous.Select(o => o.Index).SequenceEqual(seen.Select(o => o.Index)))
                {
                    if (seen.Count > 1)
                    {
                        return await Stopped(
                            $"На странице сразу несколько исходов: {Names(seen)} — непонятно, что случилось. Посмотрите на страницу " +
                            "(снимок ниже) и решите.", page);
                    }

                    var hit = seen[0];
                    var output = Output(hit.Index, hit.Name, page.Url);
                    return NodeResult.Ok(OutcomePort(hit.Index), Updates(context, output), output, $"Исход «{hit.Name}».");
                }

                previous = seen;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    // Исход, впервые замеченный на последней проверке, ещё не подтверждён — ему ещё одна проверка.
                    if (seen.Count > 0 && !lastLook)
                    {
                        lastLook = true;
                        await Task.Delay(PollInterval, cancellationToken);
                        continue;
                    }

                    // Что-то появлялось, но так и не продержалось две проверки подряд: «ничего не появилось» было бы неправдой, а
                    // ветка «Ничего из этого» могла бы повторить действие, которое уже сработало.
                    if (glimpsed.Count > 0)
                    {
                        return await Stopped(
                            $"За {wait.TotalSeconds:0} с исход не установился: {Names(glimpsed.Values)} появлялся, но страница ещё " +
                            "менялась — непонятно, что случилось. Посмотрите на страницу (снимок ниже) и решите.", page);
                    }

                    var output = Output(0, null, page.Url);
                    var none = NodeResult.Ok(Ports.Timeout, Updates(context, output), output,
                        $"За {wait.TotalSeconds:0} с не появилось ни одного исхода ({Names(outcomes)}).");

                    // Снимок нужен, только если «Ничего из этого» не подключён (это остановка); иначе он занял бы место снимков
                    // настоящих ошибок процесса.
                    return context.Graph?.Next(context.Node.Id, Ports.Timeout) is null
                        ? await BrowserNodes.WithSnapshotAsync(none, sessions, context, page, cancellationToken)
                        : none;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            // Снимок — той вкладки, на которую блок смотрел (она могла смениться за время ожидания).
            async Task<NodeResult> Stopped(string error, IPage current) =>
                await BrowserNodes.WithSnapshotAsync(NodeResult.Fail(error, retryable: false), sessions, context, current, cancellationToken);
        }, "ждать одно из");

    private static string Names(IEnumerable<Outcome> outcomes) => string.Join(", ", outcomes.Select(o => $"«{o.Name}»"));

    /// <summary>Виден ли исход сейчас — без ожидания.</summary>
    private static async Task<bool> SeenAsync(IPage page, Outcome outcome)
    {
        switch (outcome.Kind)
        {
            case "url":
                return page.Url.Contains(outcome.Target, StringComparison.OrdinalIgnoreCase);
            case "text":
                try
                {
                    // Неразрывный пробел («не&nbsp;найден») и переносы строк на странице — обычные пробелы: фразу вводят или
                    // копируют со страницы (при копировании неразрывный пробел становится обычным).
                    var text = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 });
                    return TextMatching.Matches(Spaces().Replace(text, " "), Spaces().Replace(outcome.Target, " "), TextMatching.Contains);
                }
                catch (TimeoutException)
                {
                    // Страница ещё загружается.
                    return false;
                }

            default:
                // Любой видимый: первым совпадением бывает скрытый шаблон (мобильное меню, сообщение об ошибке другого поля).
                return await page.Locator(outcome.Target).Filter(new LocatorFilterOptions { Visible = true }).First.IsVisibleAsync();
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static JsonObject Output(int index, string? name, string url) => new() { ["outcome"] = index, ["name"] = name, ["url"] = url };

    private static Dictionary<string, JsonNode?> Updates(NodeExecutionContext context, JsonObject output) =>
        context.GetString("saveTo") is { Length: > 0 } saveTo ? new() { [saveTo] = output.DeepClone() } : [];
}
