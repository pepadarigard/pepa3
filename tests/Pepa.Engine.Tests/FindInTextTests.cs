using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Serialization;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Блок «Найти в тексте» на настоящем движке.</summary>
public sealed class FindInTextTests
{
    private static ScenarioDefinition Find(string text, object config, bool connectNotFound = true)
    {
        var settings = new JsonObject { ["text"] = text, ["saveTo"] = "found" };
        foreach (var (key, value) in (JsonObject)JsonSerializer.SerializeToNode(config, PepaJson.Options)!)
        {
            settings[key] = value?.DeepClone();
        }

        var builder = new ScenarioBuilder("Найти")
            .Variable("found", VariableType.Any)
            .Node("start", "start")
            .Node("find", "text.find", settings)
            .Node("yes", "end", new { message = "есть" })
            .Edge("start", "find").Edge("find", "yes", Ports.True);
        if (connectNotFound)
        {
            builder.Node("no", "end", new { message = "нет" }).Edge("find", "no", Ports.False);
        }

        return builder.Build();
    }

    private static async Task<ProcessRecord> RunAsync(ScenarioDefinition scenario, ProcessStatus expected = ProcessStatus.Completed)
    {
        await using var h = await EngineHarness.CreateAsync();
        return await h.WaitForStatusAsync(await h.RunAsync(scenario), expected);
    }

    [Theory]
    [InlineData("Ваш код: 48213. Никому не сообщайте.", "48213")]
    [InlineData("Код для входа 7731-\n2026 год", "7731")]
    [InlineData("Номер 12345678901 и код 5521", "5521")]
    public async Task Code_is_a_separate_group_of_digits(string text, string expected)
    {
        var done = await RunAsync(Find(text, new { what = "code", minDigits = 4, maxDigits = 6 }));
        Assert.Equal("есть", done.StatusMessage);
        Assert.Equal(expected, done.Variables["found"]!.GetValue<string>());
    }

    [Fact]
    public async Task Not_found_goes_to_its_branch_or_stops_with_reason()
    {
        var done = await RunAsync(Find("Кода нет", new { what = "code" }));
        Assert.Equal("нет", done.StatusMessage);

        var failed = await RunAsync(Find("Кода нет", new { what = "code" }, connectNotFound: false), ProcessStatus.NeedsAttention);
        Assert.Contains("не найдено: код из 4–8 цифр", failed.StatusMessage ?? failed.LastError?.ToJsonString());
    }

    [Fact]
    public async Task Numbers_links_emails_and_text_between_words()
    {
        Assert.Equal(1499.5, (await RunAsync(Find("Итого: 1499,50 руб.", new { what = "number" }))).Variables["found"]!.GetValue<double>());

        var links = (await RunAsync(Find("Ссылки: https://a.example/x?y=1, и (https://b.example/путь).", new { what = "link", which = "all" })))
            .Variables["found"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["https://a.example/x?y=1", "https://b.example/путь"], links);

        Assert.Equal("ivan.petrov+shop@mail.example.ru",
            (await RunAsync(Find("Пишите: ivan.petrov+shop@mail.example.ru.", new { what = "email" }))).Variables["found"]!.GetValue<string>());

        Assert.Equal("A-100", (await RunAsync(Find("Заказ: A-100\nСтатус: готов", new { what = "between", after = "заказ:" })))
            .Variables["found"]!.GetValue<string>());
        Assert.Equal("готов", (await RunAsync(Find("Статус: готов. Спасибо", new { what = "between", after = "Статус:", before = "." })))
            .Variables["found"]!.GetValue<string>());
        Assert.Equal("второй", (await RunAsync(Find("[первый] [второй]", new { what = "between", after = "[", before = "]", which = "last" })))
            .Variables["found"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Сумма 1,000,000 руб.", 1000000)]
    [InlineData("Сумма 1 234,56 ₽", 1234.56)]
    [InlineData("Сумма 1\u00A0234\u00A0567 ₽", 1234567)]
    [InlineData("Total: 1,234.56 USD", 1234.56)]
    [InlineData("Summe 1.234,56 €", 1234.56)]
    [InlineData("Скидка 12,5%", 12.5)]
    [InlineData("Баланс: -40.25", -40.25)]
    [InlineData("с 10-20 мая", 10)]
    public async Task Numbers_with_group_separators(string text, double expected) =>
        Assert.Equal(expected, (await RunAsync(Find(text, new { what = "number" }))).Variables["found"]!.GetValue<double>());

    [Fact]
    public async Task Only_ordinary_digits_count_and_soft_hyphen_does_not_shift_the_result()
    {
        // Арабские и полноширинные цифры — не «код» (и не роняют разбор чисел).
        Assert.Equal("нет", (await RunAsync(Find("Код: ٤٨٢١ или ４８２１", new { what = "code" }))).StatusMessage);
        Assert.Equal("нет", (await RunAsync(Find("Сумма ٤٨٢١", new { what = "number" }))).StatusMessage);
        // Мягкий перенос в тексте: найденное — ровно то, что после метки.
        Assert.Equal("4821", (await RunAsync(Find("Ко\u00ADд: 4821", new { what = "between", after = "Ко\u00ADд:" })))
            .Variables["found"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pattern_with_an_empty_optional_group_is_not_a_find()
    {
        Assert.Equal("нет", (await RunAsync(Find("Заказ принят", new { what = "pattern", pattern = @"заказ\s*(\d+)?" }))).StatusMessage);
        Assert.Equal("15", (await RunAsync(Find("Заказ принят. Заказ 15", new { what = "pattern", pattern = @"заказ\s*(\d+)?" })))
            .Variables["found"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pattern_uses_first_group_and_reports_bad_pattern()
    {
        Assert.Equal("777", (await RunAsync(Find("ЗАКАЗ №777 принят", new { what = "pattern", pattern = @"заказ №(\d+)" })))
            .Variables["found"]!.GetValue<string>());

        var failed = await RunAsync(Find("текст", new { what = "pattern", pattern = "(незакрытая" }), ProcessStatus.NeedsAttention);
        Assert.Contains("Шаблон с ошибкой", failed.StatusMessage ?? failed.LastError?.ToJsonString());
    }
}
