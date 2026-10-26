using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Nodes;

/// <summary>
/// «Найти в тексте»: код из цифр, число, ссылку, почту, текст между словами или по шаблону — без JavaScript.
/// Типичный случай: достать код из сообщения Telegram или со страницы и ввести его дальше.
/// </summary>
public sealed class FindInTextNode : INodeExecutor
{
    public const string TypeName = "text.find";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly FieldCondition IsCode = new("what", ["code"]);
    private static readonly FieldCondition IsBetween = new("what", ["between"]);
    private static readonly FieldCondition IsPattern = new("what", ["pattern"]);

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Найти в тексте",
        Description = "Находит в тексте код из цифр, число, ссылку, почту или текст между словами и сохраняет в переменную. " +
                      "Например, код подтверждения из сообщения Telegram.",
        Icon = "text-search",
        SummaryField = "what",
        Outputs =
        [
            new PortDescriptor(Ports.True, "Найдено"),
            new PortDescriptor(Ports.False, "Не найдено", PortKind.Optional),
        ],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "text", Label = "Где искать", Kind = FieldKind.MultilineTemplate, Required = true,
                Placeholder = "{{ vars.reply.payload.text }}",
                Help = "Текст или переменная, например {{ vars.reply.payload.text }} — текст сообщения из «Ждать сообщение».",
            },
            new FieldDescriptor
            {
                Name = "what", Label = "Что найти", Kind = FieldKind.Select, Default = "code",
                Options =
                [
                    new("code", "Код из цифр"), new("number", "Число"), new("link", "Ссылку"), new("email", "Адрес почты"),
                    new("between", "Текст между словами"), new("pattern", "По шаблону (регулярное выражение)"),
                ],
            },
            new FieldDescriptor
            {
                Name = "minDigits", Label = "Цифр в коде: от", Kind = FieldKind.Number, Default = 4, Min = 1, Max = 32,
                VisibleWhen = IsCode,
            },
            new FieldDescriptor
            {
                Name = "maxDigits", Label = "до", Kind = FieldKind.Number, Default = 8, Min = 1, Max = 32,
                VisibleWhen = IsCode,
                Help = "Код ищется как отдельная группа цифр: в «12345678901» код из 4–8 цифр не найдётся.",
            },
            new FieldDescriptor
            {
                Name = "after", Label = "После слов", Kind = FieldKind.Template, Placeholder = "Ваш код:",
                VisibleWhen = IsBetween,
                Help = "Пусто — с начала текста.",
            },
            new FieldDescriptor
            {
                Name = "before", Label = "До слов", Kind = FieldKind.Template, Placeholder = ".",
                VisibleWhen = IsBetween,
                Help = "Пусто — до конца строки.",
            },
            new FieldDescriptor
            {
                Name = "pattern", Label = "Шаблон", Kind = FieldKind.Text, Required = true, Placeholder = @"заказ №(\d+)",
                VisibleWhen = IsPattern,
                Help = "Регулярное выражение .NET. Если в шаблоне есть скобки — берётся первая группа.",
            },
            new FieldDescriptor
            {
                Name = "which", Label = "Какое совпадение", Kind = FieldKind.Select, Default = "first",
                Options = [new("first", "Первое"), new("last", "Последнее"), new("all", "Все (список)")],
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить в", Kind = FieldKind.Variable, Required = true,
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var text = context.GetString("text") ?? "";
        var what = context.GetString("what") ?? "code";
        var which = context.GetString("which") ?? "first";
        var saveTo = context.RequireString("saveTo");

        var found = Find(context, text, what);
        if (found.Count == 0)
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.False, new Dictionary<string, JsonNode?>(), null,
                $"В тексте не найдено: {Describe(context, what)}."));
        }

        JsonNode? value = which switch
        {
            "all" => new JsonArray([.. found.Select(f => f.DeepClone())]),
            "last" => found[^1].DeepClone(),
            _ => found[0].DeepClone(),
        };
        var shown = which == "all" ? $"{found.Count} шт." : $"«{Preview(value!.ToString())}»";
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.True, new Dictionary<string, JsonNode?> { [saveTo] = value },
            new JsonObject { ["value"] = value?.DeepClone(), ["count"] = found.Count }, $"Найдено {Describe(context, what)}: {shown}"));
    }

    private static List<JsonNode> Find(NodeExecutionContext context, string text, string what)
    {
        switch (what)
        {
            case "code":
            {
                var min = (int)(context.GetNumber("minDigits") ?? 4);
                var max = (int)(context.GetNumber("maxDigits") ?? 8);
                if (min < 1 || max < min || max > 32)
                {
                    throw new NodeInputException("Длина кода: «от» — не меньше 1, «до» — не меньше «от» и не больше 32.");
                }

                // Только цифры 0–9: «\d» в .NET находит и арабские, и полноширинные цифры.
                return Matches(text, $@"(?<![0-9])[0-9]{{{min},{max}}}(?![0-9])", RegexOptions.None).Select(m => (JsonNode)JsonValue.Create(m)).ToList();
            }

            case "number":
                return Matches(text, NumberPattern, RegexOptions.None).Select(m => (JsonNode)JsonValue.Create(ParseNumber(m))).ToList();

            case "link":
                return Matches(text, @"https?://[^\s<>""«»]+", RegexOptions.IgnoreCase)
                    .Select(m => (JsonNode)JsonValue.Create(m.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'')))
                    .ToList();

            case "email":
                return Matches(text, @"[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.\p{L}{2,}", RegexOptions.None)
                    .Select(m => (JsonNode)JsonValue.Create(m)).ToList();

            case "between":
                return Between(text, context.GetString("after") ?? "", context.GetString("before") ?? "")
                    .Select(m => (JsonNode)JsonValue.Create(m)).ToList();

            case "pattern":
            {
                var pattern = context.RequireString("pattern");
                Regex regex;
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new NodeInputException($"Шаблон с ошибкой: {ex.Message}");
                }

                try
                {
                    // Со скобками — значение первой группы; совпадение, где группа пуста или не участвовала, не считается находкой.
                    return regex.Matches(text)
                        .Select(m => m.Groups.Count > 1 ? m.Groups[1].Success ? m.Groups[1].Value : "" : m.Value)
                        .Where(v => v.Length > 0)
                        .Select(v => (JsonNode)JsonValue.Create(v))
                        .ToList();
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new NodeInputException("Шаблон слишком сложный для этого текста (поиск не уложился в секунду). Упростите шаблон.");
                }
            }

            default:
                throw new NodeInputException($"Неизвестный вид поиска «{what}».");
        }
    }

    /// <summary>
    /// Число: с разделителями разрядов (1 234 567,89 · 1,000,000 · 1.234,56 · 1,234.56) или без (1234 · 12,5 · 12.5).
    /// Одна запятая или одна точка — дробная часть: «1,234» — это 1,234 (так пишут по-русски).
    /// </summary>
    private const string NumberPattern =
        @"(?<![0-9.,])-?(?:" +
        @"[0-9]{1,3}(?:[ \u00A0\u202F\u2009][0-9]{3})+(?:[.,][0-9]+)?" +
        @"|[0-9]{1,3}(?:,[0-9]{3}){2,}(?:\.[0-9]+)?" +
        @"|[0-9]{1,3}(?:,[0-9]{3})+\.[0-9]+" +
        @"|[0-9]{1,3}(?:\.[0-9]{3}){2,}(?:,[0-9]+)?" +
        @"|[0-9]{1,3}(?:\.[0-9]{3})+,[0-9]+" +
        @"|[0-9]+(?:[.,][0-9]+)?" +
        @")(?![0-9])";

    private static double ParseNumber(string match)
    {
        var s = match.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace("\u2009", "");
        var commas = s.Count(c => c == ',');
        var dots = s.Count(c => c == '.');
        if (commas > 0 && dots > 0)
        {
            // Есть и то и другое: последний знак — дробная часть, первый — разряды.
            s = s.LastIndexOf(',') > s.LastIndexOf('.') ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        }
        else if (commas > 1)
        {
            s = s.Replace(",", "");
        }
        else if (dots > 1)
        {
            s = s.Replace(".", "");
        }
        else
        {
            s = s.Replace(',', '.');
        }

        return double.Parse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> Matches(string text, string pattern, RegexOptions options) =>
        Regex.Matches(text, pattern, options | RegexOptions.CultureInvariant, RegexTimeout).Select(m => m.Value);

    /// <summary>Все фрагменты между «после» и «до» (без учёта регистра); пустое «до» — до конца строки.</summary>
    private static IEnumerable<string> Between(string text, string after, string before)
    {
        var position = 0;
        while (position <= text.Length)
        {
            int start;
            if (after.Length == 0)
            {
                start = position;
            }
            else
            {
                // Без учёта культуры: «культурный» поиск пропускает мягкие переносы и подобные знаки, и позиция конца находки
                // не совпадала бы с длиной искомого.
                var index = text.IndexOf(after, position, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    yield break;
                }

                start = index + after.Length;
            }

            int end;
            if (before.Length == 0)
            {
                end = text.IndexOfAny(['\r', '\n'], start);
                end = end < 0 ? text.Length : end;
            }
            else
            {
                end = text.IndexOf(before, start, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    yield break;
                }
            }

            var value = text[start..end].Trim();
            if (value.Length > 0)
            {
                yield return value;
            }

            // Без «после» — только один фрагмент (иначе каждый следующий начинался бы сразу за предыдущим).
            if (after.Length == 0)
            {
                yield break;
            }

            position = Math.Max(end + before.Length, start + 1);
        }
    }

    private static string Describe(NodeExecutionContext context, string what) => what switch
    {
        "code" => $"код из {(int)(context.GetNumber("minDigits") ?? 4)}–{(int)(context.GetNumber("maxDigits") ?? 8)} цифр",
        "number" => "число",
        "link" => "ссылка",
        "email" => "адрес почты",
        "between" => "текст между словами",
        _ => "совпадение с шаблоном",
    };

    private static string Preview(string text) => text.Length > 60 ? text[..60] + "…" : text;
}
