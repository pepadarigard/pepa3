namespace Pepa.Core.Expressions;

/// <summary>Разбор шаблона вида <c>Привет, {{ vars.name }}!</c> на текст и выражения.</summary>
public static class TemplateParser
{
    public abstract record Segment;

    public sealed record Literal(string Text) : Segment;

    public sealed record Expression(string Code) : Segment;

    public static IReadOnlyList<Segment> Parse(string template)
    {
        var result = new List<Segment>();
        var position = 0;
        while (position < template.Length)
        {
            var open = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                result.Add(new Literal(template[position..]));
                break;
            }

            if (open > position)
            {
                result.Add(new Literal(template[position..open]));
            }

            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ExpressionException("Не закрыта подстановка: после «{{» нет «}}».");
            }

            var code = template[(open + 2)..close].Trim();
            if (code.Length == 0)
            {
                throw new ExpressionException("Пустая подстановка «{{ }}».");
            }

            result.Add(new Expression(code));
            position = close + 2;
        }

        return result;
    }

    public static bool HasExpressions(string template) => template.Contains("{{", StringComparison.Ordinal);

    /// <summary>
    /// Подстановки шаблона ничего не дали: от значения остался только текст самого шаблона («Код для {{ vars.login }}» с
    /// пустым login → «Код для»). Для условий ожидания это значит «подойдёт чужое» (письмо, сообщение, файл другого процесса),
    /// поэтому такие значения отвергаются. Пробелы не учитываются.
    /// </summary>
    public static bool OnlyLiteralLeft(string? template, string? rendered)
    {
        if (template is null || rendered is null || !HasExpressions(template))
        {
            return false;
        }

        var literal = string.Concat(Parse(template).OfType<Literal>().Select(l => l.Text));
        return Squeeze(rendered) == Squeeze(literal);
    }

    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
