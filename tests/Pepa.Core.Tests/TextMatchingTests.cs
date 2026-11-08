using Pepa.Core.Text;

namespace Pepa.Core.Tests;

public sealed class TextMatchingTests
{
    [Theory]
    [InlineData("Готово!", "готово", true)]
    [InlineData("  ГОТОВО 👍 ", "готово", true)]
    [InlineData("ещё не готово", "готово", false)]
    [InlineData("Ёлка", "елка", true)]
    [InlineData("готово, спасибо", "готово", false)]
    public void Exact_ignores_case_yo_and_edge_punctuation(string text, string option, bool expected) =>
        Assert.Equal(expected, TextMatching.Matches(text, option, TextMatching.Exact));

    [Theory]
    [InlineData("Ваш код: 1234", "код", true)]
    [InlineData("кодовое слово", "код", false)]
    [InlineData("ещё не готово", "готово", true)]
    [InlineData("всё ГОТОВО.", "готово", true)]
    public void Word_needs_whole_words(string text, string option, bool expected) =>
        Assert.Equal(expected, TextMatching.Matches(text, option, TextMatching.Word));

    [Theory]
    [InlineData("Готово, спасибо", "готово", true)]
    [InlineData("готовность 90%", "готово", false)]
    [InlineData("нет, не сейчас", "нет", true)]
    [InlineData("не готово", "готово", false)]
    public void Starts_needs_the_option_as_leading_words(string text, string option, bool expected) =>
        Assert.Equal(expected, TextMatching.Matches(text, option, TextMatching.Starts));

    [Fact]
    public void Option_of_symbols_only_is_compared_as_it_is()
    {
        Assert.True(TextMatching.Matches("+", "+", TextMatching.Exact));
        Assert.True(TextMatching.Matches(" 👍 ", "👍", TextMatching.Word));
        Assert.True(TextMatching.Matches("好的", "好的", TextMatching.Starts));
        Assert.False(TextMatching.Matches("++", "+", TextMatching.Exact));
        Assert.False(TextMatching.Matches("+ согласен", "+", TextMatching.Exact));
        Assert.False(TextMatching.Matches("что угодно", "  ", TextMatching.Word));
    }
}
