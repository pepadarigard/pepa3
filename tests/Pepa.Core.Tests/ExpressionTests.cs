using System.Text.Json.Nodes;
using Pepa.Core.Expressions;

namespace Pepa.Core.Tests;

public sealed class ExpressionTests
{
    private readonly JintExpressionEvaluator _evaluator = new(new ExpressionLimits { Timeout = TimeSpan.FromMilliseconds(500) });

    private static ExpressionScope Scope(JsonObject? vars = null) => new(vars ?? new JsonObject());

    [Fact]
    public void Evaluates_arithmetic_strings_and_variables()
    {
        var scope = Scope(new JsonObject { ["count"] = 2, ["name"] = "Пепа", ["items"] = new JsonArray(1, 2, 3) });

        Assert.Equal(5, _evaluator.Evaluate("vars.count + 3", scope)!.GetValue<double>());
        Assert.Equal("Привет, Пепа", _evaluator.Evaluate("'Привет, ' + vars.name", scope)!.GetValue<string>());
        Assert.Equal(3, _evaluator.Evaluate("vars.items.length", scope)!.GetValue<double>());
        Assert.Equal("[2,4,6]", _evaluator.Evaluate("vars.items.map(x => x * 2)", scope)!.ToJsonString());
        Assert.Null(_evaluator.Evaluate("vars.missing", scope));
    }

    [Fact]
    public void Condition_requires_boolean()
    {
        var scope = Scope(new JsonObject { ["count"] = 2, ["text"] = "false" });

        Assert.True(_evaluator.EvaluateCondition("vars.count > 1", scope));
        Assert.False(_evaluator.EvaluateCondition("vars.count === 3", scope));
        var error = Assert.Throws<ExpressionException>(() => _evaluator.EvaluateCondition("vars.text", scope));
        Assert.Contains("true/false", error.Message);
        Assert.Contains("строка", error.Message);
    }

    [Fact]
    public void Renders_templates()
    {
        var scope = Scope(new JsonObject { ["n"] = 3, ["name"] = "мир", ["obj"] = new JsonObject { ["a"] = 1 } });

        Assert.Equal("Привет, мир!", _evaluator.RenderTemplate("Привет, {{ vars.name }}!", scope));
        Assert.Equal("Файл 3.txt", _evaluator.RenderTemplate("Файл {{vars.n}}.txt", scope));
        Assert.Equal("Пусто: []", _evaluator.RenderTemplate("Пусто: [{{ vars.nothing }}]", scope));
        Assert.Equal("{\"a\":1}", _evaluator.RenderTemplate("{{ vars.obj }}", scope));
        Assert.Equal("без подстановок", _evaluator.RenderTemplate("без подстановок", scope));
    }

    [Fact]
    public void Mutations_inside_expression_do_not_leak()
    {
        var vars = new JsonObject { ["count"] = 1 };
        _evaluator.Evaluate("(vars.count = 100, vars.count)", Scope(vars));

        Assert.Equal(1, vars["count"]!.GetValue<int>());
        Assert.Equal(1, _evaluator.Evaluate("vars.count", Scope(vars))!.GetValue<double>());
    }

    [Fact]
    public void Endless_loop_is_stopped()
    {
        var error = Assert.Throws<ExpressionException>(() =>
            _evaluator.Evaluate("(() => { while (true) {} })()", Scope()));
        Assert.Contains("«(() => { while (true) {} })()»", error.Message);
    }

    [Fact]
    public void Host_environment_is_not_accessible()
    {
        Assert.Equal("undefined", _evaluator.Evaluate("typeof System", Scope())!.GetValue<string>());
        Assert.Equal("undefined", _evaluator.Evaluate("typeof importNamespace", Scope())!.GetValue<string>());
        Assert.Equal("undefined", _evaluator.Evaluate("typeof require", Scope())!.GetValue<string>());
        Assert.Equal("undefined", _evaluator.Evaluate("typeof fetch", Scope())!.GetValue<string>());
    }

    [Fact]
    public void Syntax_errors_are_reported_before_execution()
    {
        Assert.Null(_evaluator.CheckExpression("vars.a > 1 && vars.b < 2"));
        Assert.NotNull(_evaluator.CheckExpression("vars.a >"));
        Assert.NotNull(_evaluator.CheckExpression("vars.a; vars.b"));
        Assert.NotNull(_evaluator.CheckExpression("  "));
        Assert.Null(_evaluator.CheckTemplate("Привет, {{ vars.name }}"));
        Assert.NotNull(_evaluator.CheckTemplate("Привет, {{ vars.name"));
        Assert.NotNull(_evaluator.CheckTemplate("Привет, {{ vars.name + }}"));
        Assert.NotNull(_evaluator.CheckTemplate("Пусто {{ }}"));
    }

    [Fact]
    public void Runtime_errors_have_readable_message()
    {
        var error = Assert.Throws<ExpressionException>(() => _evaluator.Evaluate("vars.a.b", Scope()));
        Assert.StartsWith("Ошибка в выражении «vars.a.b»", error.Message);
    }

    [Fact]
    public void Last_error_and_process_are_available()
    {
        var scope = new ExpressionScope(new JsonObject(), new JsonObject { ["message"] = "нет файла" },
            new JsonObject { ["id"] = "p1" });

        Assert.Equal("нет файла", _evaluator.Evaluate("lastError.message", scope)!.GetValue<string>());
        Assert.Equal("p1", _evaluator.Evaluate("process.id", scope)!.GetValue<string>());
        Assert.Null(_evaluator.Evaluate("lastError", Scope()));
    }

    [Fact]
    public void Url_template_encodes_values_in_the_path_and_query_but_not_the_site()
    {
        var scope = Scope(new JsonObject
        {
            ["base"] = "https://api.example", ["id"] = "1?admin=true#", ["up"] = "../../admin", ["text"] = "кот & пёс", ["empty"] = "",
        });

        Assert.Equal("https://api.example/orders/1%3Fadmin%3Dtrue%23", _evaluator.RenderUrl("{{ vars.base }}/orders/{{ vars.id }}", scope));
        Assert.Equal("https://api.example/orders/..%2F..%2Fadmin", _evaluator.RenderUrl("https://api.example/orders/{{ vars.up }}", scope));
        Assert.Equal("https://api.example/find?q=%D0%BA%D0%BE%D1%82%20%26%20%D0%BF%D1%91%D1%81",
            _evaluator.RenderUrl("https://api.example/find?q={{ vars.text }}", scope));
        var error = Assert.Throws<ExpressionException>(() => _evaluator.RenderUrl("https://api.example/items/{{ vars.missing }}", scope));
        Assert.Contains("пустой", error.Message);
        Assert.Throws<ExpressionException>(() => _evaluator.RenderUrl("https://api.example/items/{{ vars.empty }}", scope));
    }

    [Theory]
    [InlineData("https://api.example/items/{{ vars.dots }}", "пути")]
    [InlineData("https://api.example/items/{{ vars.dot }}/delete", "пути")]
    [InlineData("https://{{ vars.tenant }}.example.com/api", "имени сайта")]
    [InlineData("{{ vars.base }}{{ vars.up }}", "«..»")]
    public void Url_template_refuses_values_that_would_change_the_address(string template, string message)
    {
        var scope = Scope(new JsonObject
        {
            ["dots"] = "..", ["dot"] = ".", ["tenant"] = "evil.test/#", ["base"] = "https://api.example", ["up"] = "/v1/../admin",
        });
        Assert.Contains(message, Assert.Throws<ExpressionException>(() => _evaluator.RenderUrl(template, scope)).Message);
    }

    [Fact]
    public void Url_template_allows_dots_in_parameters_a_plain_tenant_and_a_whole_path()
    {
        var scope = Scope(new JsonObject
        {
            ["dots"] = "..", ["dot"] = ".", ["tenant"] = "shop-7", ["base"] = "https://api.example", ["path"] = "/v1/orders", ["url"] = "https://a.example/x",
        });
        Assert.Equal("https://api.example/find?q=..", _evaluator.RenderUrl("https://api.example/find?q={{ vars.dots }}", scope));
        Assert.Equal("https://shop-7.example.com/api", _evaluator.RenderUrl("https://{{ vars.tenant }}.example.com/api", scope));
        Assert.Equal("https://api.example/v1/orders", _evaluator.RenderUrl("{{ vars.base }}{{ vars.path }}", scope));
        Assert.Equal(" https://a.example/x", _evaluator.RenderUrl(" {{ vars.url }}", scope));
        Assert.Equal("https://api.example/v.", _evaluator.RenderUrl("https://api.example/v{{ vars.dot }}", scope));
    }

    [Theory]
    [InlineData("https://api.example/a/{{ vars.dot }}{{ vars.dot }}/admin")]
    [InlineData("https://api.example/a/.{{ vars.dot }}/admin")]
    [InlineData("https://api.example/a/{{ vars.dot }}./admin")]
    public void Dot_segments_assembled_from_several_substitutions_are_refused(string template) =>
        Assert.Throws<ExpressionException>(() => _evaluator.RenderUrl(template, Scope(new JsonObject { ["dot"] = "." })));

    [Fact]
    public void Path_after_a_base_with_a_path_and_a_whole_query_are_kept()
    {
        var scope = Scope(new JsonObject { ["base"] = "https://api.example/v1", ["path"] = "/orders/1", ["query"] = "?email=a@b.com" });
        Assert.Equal("https://api.example/v1/orders/1", _evaluator.RenderUrl("{{ vars.base }}{{ vars.path }}", scope));
        Assert.Equal("https://api.example/v1?email=a@b.com", _evaluator.RenderUrl("{{ vars.base }}{{ vars.query }}", scope));
        // После постоянной части пути «?» в значении — часть значения (кодируется), а не начало параметров.
        Assert.Equal("https://api.example/v1/find%3Femail%3Da%40b.com", _evaluator.RenderUrl("{{ vars.base }}/find{{ vars.query }}", scope));
    }

    [Theory]
    [InlineData("https://api.example/orders/{{ vars.a }}{{ vars.b }}/items", "#", "https://api.example/orders/5%23/items")]
    [InlineData("https://api.example/search?q={{ vars.a }}{{ vars.b }}", "?y&role=admin", "https://api.example/search?q=5%3Fy%26role%3Dadmin")]
    [InlineData("https://api.example/orders/{{ vars.a }}{{ vars.b }}", "/delete", "https://api.example/orders/5%2Fdelete")]
    public void Value_after_another_value_inside_the_path_is_encoded(string template, string b, string expected) =>
        Assert.Equal(expected, _evaluator.RenderUrl(template, Scope(new JsonObject { ["a"] = "5", ["b"] = b })));
}

