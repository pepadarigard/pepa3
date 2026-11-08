using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Text;

namespace Pepa.Engine.Nodes;

/// <summary>
/// «Выбор варианта»: ветвление по значению без JavaScript (D-083). Значение сравнивается с вариантами так, как сравнил бы
/// человек; свой выход у каждого варианта, «Другое» — для всего остального. Если «Другое» не подключено, неожиданное
/// значение останавливает процесс, а не уводит его в случайную ветку; подходящих вариантов два — тоже остановка.
/// </summary>
public sealed class ChoiceNode : INodeExecutor
{
    public const string TypeName = "choice";
    public const string OtherPort = "other";
    public const int MaxOptions = 5;

    public static string OptionField(int index) => $"option{index}";

    public static string OptionPort(int index) => $"v{index}";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Выбор варианта",
        Description = "Сравнивает значение (например, ответ в Telegram) с вариантами и ведёт процесс по выходу подошедшего. " +
                      "Если не подошёл ни один — выход «Другое»; не подключён — процесс остановится. Без JavaScript.",
        Icon = "signpost",
        SummaryField = "value",
        Outputs =
        [
            .. Enumerable.Range(1, MaxOptions).Select(i =>
                new PortDescriptor(OptionPort(i), $"Вариант {i}", PortKind.Optional) { LabelField = OptionField(i) }),
            new PortDescriptor(OtherPort, "Другое", PortKind.Optional),
        ],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "value", Label = "Что сравнивать", Kind = FieldKind.Template, Required = true,
                Placeholder = "{{ vars.reply.payload.text }}",
                Help = "Текст или переменная: ответ из Telegram, письма, вопрос пользователю, текст со страницы.",
            },
            new FieldDescriptor
            {
                Name = "match", Label = "Как сравнивать", Kind = FieldKind.Select, Default = TextMatching.Exact,
                Options =
                [
                    new(TextMatching.Exact, "Совпадает с вариантом"), new(TextMatching.Word, "Есть слово или фраза целиком"),
                    new(TextMatching.Starts, "Начинается с варианта"),
                ],
                Help = "Регистр и «е»/«ё» не важны, знаки и смайлы по краям — тоже. «Совпадает»: «Готово!» подходит к «готово», " +
                       "а «ещё не готово» — нет. «Слово целиком»: «готово» найдётся и в «не готово».",
            },
            .. Enumerable.Range(1, MaxOptions).Select(i => new FieldDescriptor
            {
                Name = OptionField(i), Label = $"Вариант {i}", Kind = FieldKind.Text, Required = i == 1,
                Placeholder = i switch { 1 => "готово", 2 => "нет; отмена", _ => "" },
                Help = i == 1 ? "Несколько написаний одного варианта — через «;». Пустой вариант — выхода нет." : null,
            }),
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить выбор в", Kind = FieldKind.Variable,
                Help = "Необязательно. Объект: value (что сравнивали), option (номер варианта) и text (вариант).",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var value = context.GetString("value") ?? "";
        var mode = context.GetString("match") is { Length: > 0 } configured ? configured : TextMatching.Exact;
        if (mode is not (TextMatching.Exact or TextMatching.Word or TextMatching.Starts))
        {
            throw new NodeInputException($"«{mode}» — не вариант поля «Как сравнивать».");
        }

        var hits = new List<(int Index, string Option)>();
        for (var i = 1; i <= MaxOptions; i++)
        {
            var option = context.Node.Config.GetString(OptionField(i))?.Trim();
            if (string.IsNullOrEmpty(option))
            {
                continue;
            }

            var spellings = option.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (spellings.Any(spelling => TextMatching.Matches(value, spelling, mode)))
            {
                hits.Add((i, option));
            }
        }

        var shown = Shown(value);
        if (hits.Count > 1)
        {
            // Как и неоднозначное событие (D-017): не угадывать, какой из подошедших вариантов имелся в виду.
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail(
                $"«{shown}» подходит сразу к нескольким вариантам: {string.Join(", ", hits.Select(h => $"«{h.Option}»"))}. " +
                "Уточните варианты, чтобы подходил один.", retryable: false));
        }

        var output = new JsonObject { ["value"] = value };
        if (hits.Count == 1)
        {
            var (index, option) = hits[0];
            output["option"] = index;
            output["text"] = option;
            return ValueTask.FromResult<NodeResult>(NodeResult.Ok(OptionPort(index), Updates(context, output), output,
                $"«{shown}» — вариант «{option}»."));
        }

        output["option"] = null;
        output["text"] = null;
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(OtherPort, Updates(context, output), output,
            value.Trim().Length == 0 ? "Значение пустое — ни один вариант не подошёл." : $"«{shown}» не подошло ни к одному варианту."));
    }

    private static Dictionary<string, JsonNode?> Updates(NodeExecutionContext context, JsonObject output) =>
        context.GetString("saveTo") is { Length: > 0 } saveTo ? new() { [saveTo] = output.DeepClone() } : [];

    private static string Shown(string value)
    {
        var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 80 ? line[..79] + "…" : line;
    }
}
