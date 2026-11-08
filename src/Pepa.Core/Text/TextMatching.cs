namespace Pepa.Core.Text;

/// <summary>
/// Сравнение ответов «как человек»: без учёта регистра и «е»/«ё». «Совпадает» — весь текст без знаков и смайлов по краям;
/// «слово целиком» — вхождение, по краям которого не буква и не цифра; «начинается с» — текст начинается этими словами.
/// </summary>
public static class TextMatching
{
    public const string Exact = "exact";
    public const string Word = "word";
    public const string Starts = "starts";
    public const string Contains = "contains";

    /// <summary>Буква, цифра или «_» — часть слова.</summary>
    public static bool IsWordChar(char c) => char.ToLowerInvariant(c) != char.ToUpperInvariant(c) || c is >= '0' and <= '9' or '_';

    /// <summary>Строчные, «ё» — «е».</summary>
    public static string Fold(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Строчные, «ё» — «е», без знаков и смайлов по краям.</summary>
    public static string Normalize(string text)
    {
        var lower = Fold(text);
        var start = 0;
        var end = lower.Length;
        while (start < end && !IsWordChar(lower[start]))
        {
            start++;
        }

        while (end > start && !IsWordChar(lower[end - 1]))
        {
            end--;
        }

        return lower[start..end];
    }

    /// <summary>Подходит ли текст к ожидаемому. Ожидаемое без букв и цифр не подходит ни к чему.</summary>
    public static bool Matches(string text, string expected, string mode)
    {
        var wanted = Normalize(expected);
        if (wanted.Length == 0)
        {
            return false;
        }

        switch (mode)
        {
            case Exact:
                return Normalize(text) == wanted;
            case Starts:
            {
                var normalized = Normalize(text);
                return normalized.StartsWith(wanted, StringComparison.Ordinal) &&
                       (normalized.Length == wanted.Length || !IsWordChar(normalized[wanted.Length]));
            }

            case Word:
            {
                var folded = Fold(text);
                for (var i = folded.IndexOf(wanted, StringComparison.Ordinal); i >= 0; i = folded.IndexOf(wanted, i + 1, StringComparison.Ordinal))
                {
                    var before = i == 0 || !IsWordChar(folded[i - 1]);
                    var after = i + wanted.Length == folded.Length || !IsWordChar(folded[i + wanted.Length]);
                    if (before && after)
                    {
                        return true;
                    }
                }

                return false;
            }

            case Contains:
                return Fold(text).Contains(wanted, StringComparison.Ordinal);
            default:
                throw new ArgumentException($"«{mode}» — не способ сравнения.", nameof(mode));
        }
    }
}
