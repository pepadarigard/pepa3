using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Секретные переменные: значения не попадают в журнал, ошибки и итоги шагов, но процесс работает с настоящими.</summary>
public sealed class SecretTests
{
    [Fact]
    public async Task Secret_values_are_masked_in_the_journal_errors_and_status_but_the_step_keeps_the_real_value()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("S3cr3t-Pa55", ActionMode.Fail);
        var scenario = new ScenarioBuilder("Вход")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("login", VariableType.Text, isInput: true)
            .Node("start", "start")
            .Node("log", "log", new { message = "вход {{ vars.login }} / {{ vars.password }}" })
            .Node("act", TestActionNode.TypeName, new { key = "{{ vars.password }}" })
            .Node("end", "end", new { message = "готово" })
            .Chain("start", "log", "act", "end").Build();

        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "S3cr3t-Pa55", ["login"] = "ivan" });
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.DoesNotContain("S3cr3t-Pa55", stopped.StatusMessage);
        Assert.Contains(ProcessEngine.SecretMask, stopped.StatusMessage);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message));
        Assert.Contains("вход ivan / ••••", journal);
        Assert.DoesNotContain("S3cr3t-Pa55", journal);
        var steps = await h.StepsAsync(id);
        var failed = steps.Single(s => s.NodeId == "act");
        Assert.DoesNotContain("S3cr3t-Pa55", failed.Error);
        // Параметры шага и переменные — настоящие: по ним процесс продолжается и проверяет результат после перезапуска.
        Assert.Equal("S3cr3t-Pa55", failed.Input!["key"]!.GetValue<string>());
        Assert.Equal("S3cr3t-Pa55", stopped.Variables["password"]!.GetValue<string>());
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Longer_secret_is_masked_whole_and_very_short_values_are_left_alone()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Коды")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("pin", VariableType.Text, isInput: true, secret: true)
            .Variable("code", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("end", "end", new { message = "{{ vars.password }} {{ vars.pin }} {{ vars.code }}" })
            .Chain("start", "end").Build();

        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "abc123", ["pin"] = "abc", ["code"] = "7" });
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        // «abc» внутри «abc123» не оставляет хвост «123»; одну цифру не скрыть — она есть в любом тексте.
        Assert.Equal("•••• •••• 7", done.StatusMessage);
    }

    [Fact]
    public async Task Called_scenario_keeps_passed_secrets_secret_and_its_result_stays_real_but_hidden()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("Ребёнок")
            .Variable("password", VariableType.Text, isInput: true)
            .Variable("token", VariableType.Text, secret: true)
            .Node("start", "start")
            .Node("log", "log", new { message = "в ребёнке {{ vars.password }}" })
            .Node("token", "variable.set", new { variable = "token", value = "'tok-7f3a9c2e'" })
            .Node("end", "end")
            .Chain("start", "log", "token", "end").Build());
        var parentScenario = new ScenarioBuilder("Родитель")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("result", VariableType.Any)
            .Variable("header", VariableType.Text)
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = child, inputs = "({ password: vars.password })", saveTo = "result" })
            .Node("header", "variable.set", new { variable = "header", value = "'Bearer ' + vars.result.token" })
            .Node("log", "log", new { message = "заголовок {{ vars.header }}" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "header", Ports.Done).Chain("header", "log", "end")
            .Build();
        var parent = await h.RunAsync(parentScenario, new JsonObject { ["password"] = "S3cr3t-Pa55" });
        var done = await h.WaitForStatusAsync(parent, ProcessStatus.Completed);

        // Данные — настоящие: вызвавший сценарий работает с секретом вызванного (не с «••••»)...
        Assert.Equal("Bearer tok-7f3a9c2e", done.Variables["header"]!.GetValue<string>());
        Assert.Equal("S3cr3t-Pa55", done.Variables["result"]!["password"]!.GetValue<string>());
        // ...а показываются скрытыми: секреты пришли вместе с итогом.
        var shown = new SecretMasker(done, parentScenario).Variables(done.Variables).ToJsonString();
        Assert.DoesNotContain("S3cr3t-Pa55", shown);
        Assert.DoesNotContain("tok-7f3a9c2e", shown);
        var parentJournal = string.Join("\n", (await h.Store.GetJournalAsync(parent)).Select(j => j.Message));
        Assert.Contains("заголовок Bearer ••••", parentJournal);

        // Во вызванном сценарии переменная не отмечена секретной, но значение пришло из секретной — оно секретно и там.
        var called = (await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 10 })).Single(p => p.ParentProcessId == parent);
        Assert.Contains("S3cr3t-Pa55", called.Secrets!.Carried);
        var definition = Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(called.DefinitionJson);
        Assert.False(definition.Variables.Single(v => v.Name == "password").Secret);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(called.Id)).Select(j => j.Message));
        Assert.Contains("в ребёнке ••••", journal);
        Assert.Equal("S3cr3t-Pa55", called.Variables["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task Short_secret_is_passed_on_only_as_a_whole_value()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("Заказ")
            .Variable("orderId", VariableType.Text, isInput: true)
            .Variable("pin", VariableType.Text, isInput: true)
            .Node("start", "start").Node("log", "log", new { message = "заказ {{ vars.orderId }}" }).Node("end", "end")
            .Chain("start", "log", "end").Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Variable("pin", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = child, inputs = "({ orderId: 'A-12345', pin: vars.pin })" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done).Build(), new JsonObject { ["pin"] = "1234" });
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed);

        var called = (await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 10 })).Single(p => p.ParentProcessId == parent);
        Assert.Equal(["1234"], called.Secrets!.Carried);
        // ПИН «1234» не нашёлся внутри номера заказа «A-12345»: номер показывается как есть.
        Assert.Contains("заказ A-12345", string.Join("\n", (await h.Store.GetJournalAsync(called.Id)).Select(j => j.Message)));
    }

    [Fact]
    public async Task Secret_sent_with_an_event_stays_real_for_the_receiver_and_hidden_everywhere()
    {
        await using var h = await EngineHarness.CreateAsync();
        var receiverScenario = new ScenarioBuilder("Получатель")
            .Variable("got", VariableType.Any)
            .Node("start", "start")
            .Node("wait", "event.wait", new { source = "process", eventType = "credentials", saveTo = "got" })
            .Node("log", "log", new { message = "получено {{ vars.got.payload.password }}" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "log", Ports.Event).Chain("log", "end").Build();
        var receiver = await h.RunAsync(receiverScenario);
        await h.WaitForAsync(receiver, p => p.Status == ProcessStatus.Waiting);

        var sender = await h.RunAsync(new ScenarioBuilder("Отправитель")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("send", "event.send", new { source = "process", eventType = "credentials", payload = "({ password: vars.password })" })
            .Node("end", "end")
            .Chain("start", "send", "end").Build(), new JsonObject { ["password"] = "S3cr3t-Pa55" });
        await h.WaitForStatusAsync(sender, ProcessStatus.Completed);
        var got = await h.WaitForStatusAsync(receiver, ProcessStatus.Completed);

        Assert.Equal("S3cr3t-Pa55", got.Variables["got"]!["payload"]!["password"]!.GetValue<string>());
        Assert.Contains("S3cr3t-Pa55", got.Secrets!.Carried);
        Assert.Contains("получено ••••", string.Join("\n", (await h.Store.GetJournalAsync(receiver)).Select(j => j.Message)));
        Assert.DoesNotContain("S3cr3t-Pa55", new SecretMasker(got, receiverScenario).Variables(got.Variables).ToJsonString());
        var steps = (await h.StepsAsync(receiver)).Select(new SecretMasker(got, receiverScenario).Step);
        Assert.DoesNotContain("S3cr3t-Pa55", string.Join("\n", steps.Select(s => $"{s.Input?.ToJsonString()} {s.Output?.ToJsonString()}")));
    }

    [Fact]
    public async Task Copy_of_an_old_secret_and_other_ways_of_writing_its_name_stay_hidden()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Смена")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("пароль", VariableType.Text, isInput: true, secret: true)
            .Variable("oldPassword", VariableType.Text)
            .Node("start", "start")
            .Node("copy", "variable.set", new { variable = "oldPassword", value = "vars.password" })
            .Node("a", "log", new { message = "{{ vars['password'] }}" })
            .Node("b", "log", new { message = "{{ vars?.password }}" })
            .Node("c", "log", new { message = "{{ vars.пароль }}" })
            .Node("set", "variable.set", new { variable = "password", value = "'second-Pa55'" })
            .Node("clear", "variable.set", new { variable = "пароль", value = "''" })
            .Node("end", "end")
            .Chain("start", "copy", "a", "b", "c", "set", "clear", "end").Build();
        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "S3cr3t-Pa55", ["пароль"] = "Кириллица-77" });
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        var masker = new SecretMasker(done, scenario);
        var shown = masker.Variables(done.Variables).ToJsonString() + string.Join("\n",
            (await h.StepsAsync(id)).Select(masker.Step).Select(s => $"{s.Input?.ToJsonString()} {s.Output?.ToJsonString()}"));
        Assert.DoesNotContain("S3cr3t-Pa55", shown);
        Assert.DoesNotContain("Кириллица-77", shown);
        Assert.DoesNotContain(JsonEncodedTextOf("Кириллица-77"), shown);
        // Копия в обычной переменной — настоящая.
        Assert.Equal("S3cr3t-Pa55", done.Variables["oldPassword"]!.GetValue<string>());
    }

    [Fact]
    public async Task Short_secret_inside_a_sentence_travels_with_the_event()
    {
        await using var h = await EngineHarness.CreateAsync();
        var receiver = await h.RunAsync(new ScenarioBuilder("Получатель")
            .Variable("got", VariableType.Any)
            .Node("start", "start")
            .Node("wait", "event.wait", new { source = "process", eventType = "sms", saveTo = "got" })
            .Node("log", "log", new { message = "получено {{ vars.got.payload.text }}" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "log", Ports.Event).Chain("log", "end").Build());
        await h.WaitForAsync(receiver, p => p.Status == ProcessStatus.Waiting);
        await h.RunAsync(new ScenarioBuilder("Отправитель")
            .Variable("pin", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("send", "event.send", new { source = "process", eventType = "sms", payload = "({ text: 'Ваш ПИН: ' + vars.pin })" })
            .Node("end", "end")
            .Chain("start", "send", "end").Build(), new JsonObject { ["pin"] = "4821" });
        var got = await h.WaitForStatusAsync(receiver, ProcessStatus.Completed);

        Assert.Contains("4821", got.Secrets!.Carried);
        Assert.Contains("получено Ваш ПИН: ••••", string.Join("\n", (await h.Store.GetJournalAsync(receiver)).Select(j => j.Message)));
    }

    [Fact]
    public async Task Large_secret_list_is_remembered_whole_along_with_the_callers_secret()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("Коды")
            .Variable("password", VariableType.Text, isInput: true)
            .Variable("codes", VariableType.List, secret: true)
            .Node("start", "start")
            .Node("codes", "variable.set", new { variable = "codes", value = "Array.from({ length: 600 }, (_, i) => 'code-' + (100000 + i))" })
            .Node("rotate", "variable.set", new { variable = "codes", value = "['new-code-1']" })
            .Node("log", "log", new { message = "вход {{ vars.password }}, первый старый код code-100000" })
            .Node("end", "end")
            .Chain("start", "codes", "rotate", "log", "end").Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = child, inputs = "({ password: vars.password })" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done).Build(), new JsonObject { ["password"] = "S3cr3t-Pa55" });
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed);

        var called = (await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 10 })).Single(p => p.ParentProcessId == parent);
        Assert.Contains("S3cr3t-Pa55", called.Secrets!.Carried);
        // Ничего не забывается: самый первый из прежних кодов скрыт так же, как пароль вызвавшего.
        Assert.Equal(600, called.Secrets.Previous.Count);
        Assert.Contains("вход ••••, первый старый код ••••", string.Join("\n", (await h.Store.GetJournalAsync(called.Id)).Select(j => j.Message)));
    }

    [Theory]
    [InlineData("…\"password\":\"S3cr3…", "…\"password\":\"••••")]
    [InlineData("HTTP 200: получено 15 строк", "HTTP 200: получено 15 строк")]
    [InlineData("код 123456, телефон +71234567890", "код ••••, телефон +71234567890")]
    public void Short_previews_and_numbers_are_masked_only_where_they_are_the_secret(string text, string expected) =>
        Assert.Equal(expected, ProcessEngine.Redact(text, ProcessEngine.Expand(["S3cr3t", "123456"])));

    [Fact]
    public async Task Numbers_inside_a_secret_object_are_secrets_too()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Карта")
            .Variable("card", VariableType.Object, secret: true)
            .Node("start", "start")
            .Node("set", "variable.set", new { variable = "card", value = "({ number: '4111111111111111', cvv: 737, pin: { code: 4821 } })" })
            .Node("log", "log", new { message = "CVV {{ vars.card.cvv }}, ПИН {{ vars.card.pin.code }}, заказ 17374821" })
            .Node("end", "end")
            .Chain("start", "set", "log", "end").Build();
        var id = await h.RunAsync(scenario);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        // Числа — целым числом: «737» и «4821» внутри номера заказа остаются.
        Assert.Contains("CVV ••••, ПИН ••••, заказ 17374821", string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message)));
        // «Задать переменную» для секретной переменной: значение скрыто целиком.
        var set = new SecretMasker(done, scenario).Step((await h.StepsAsync(id)).Single(s => s.NodeId == "set"));
        Assert.Equal(ProcessEngine.SecretMask, set.Input!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Masking_is_linear_even_with_many_matches()
    {
        var secrets = ProcessEngine.Expand(["Qw9z-Long-Secret", "Qw9z", "aaaaaa"]);
        var text = string.Concat(Enumerable.Repeat("Qw9z-Long-Secret Qw9z aaaaaaaaaa; ", 20_000));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var masked = ProcessEngine.Redact(text, secrets)!;

        // Раньше каждая замена строила новую строку: квадратичное время на большой странице.
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
        Assert.Equal(string.Concat(Enumerable.Repeat("•••• •••• ••••; ", 20_000)), masked);
    }

    [Theory]
    [InlineData("xx abcdefghi yy", "xx •••• yy")]
    [InlineData("{\"text\":\"Ваш код:\\nQw9z\"}", "{\"text\":\"Ваш код:\\n••••\"}")]
    [InlineData("{\"text\":\"код\\u00A0Qw9z\"}", "{\"text\":\"код\\u00A0••••\"}")]
    [InlineData("Обработано 1482... строк", "Обработано 1482... строк")]
    [InlineData("коды 4821 и 14821", "коды •••• и 14821")]
    public void Overlapping_secrets_escaped_boundaries_and_previews(string text, string expected) =>
        Assert.Equal(expected, ProcessEngine.Redact(text, ProcessEngine.Expand(["abcdef", "defghi", "Qw9z", "4821"])));

    [Fact]
    public async Task Secret_written_into_json_inside_an_event_travels_with_it()
    {
        await using var h = await EngineHarness.CreateAsync();
        var receiver = await h.RunAsync(new ScenarioBuilder("Получатель")
            .Variable("got", VariableType.Any)
            .Node("start", "start")
            .Node("wait", "event.wait", new { source = "process", eventType = "json", saveTo = "got" })
            .Node("log", "log", new { message = "получено {{ vars.got.payload.raw }}" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "log", Ports.Event).Chain("log", "end").Build());
        await h.WaitForAsync(receiver, p => p.Status == ProcessStatus.Waiting);
        await h.RunAsync(new ScenarioBuilder("Отправитель")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("send", "event.send", new { source = "process", eventType = "json", payload = "({ raw: JSON.stringify({ p: vars.password }) })" })
            .Node("end", "end")
            .Chain("start", "send", "end").Build(), new JsonObject { ["password"] = "pa\"ss\\word-77" });
        var got = await h.WaitForStatusAsync(receiver, ProcessStatus.Completed);

        // В данных события пароль записан с экранированием — и всё равно узнан: получатель знает, что это секрет.
        Assert.Contains("pa\"ss\\word-77", got.Secrets!.Carried);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(receiver)).Select(j => j.Message));
        Assert.Contains("получено {\"p\":\"••••\"}", journal);
        Assert.DoesNotContain("word-77", journal);
    }

    [Fact]
    public void Masking_many_short_secrets_is_fast()
    {
        var secrets = ProcessEngine.Expand(Enumerable.Range(0, 500).Select(i => $"p{i:D3}"));
        var text = string.Concat(Enumerable.Repeat("строка журнала без секретов, но с многоточием… ", 50));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 100; i++)
        {
            ProcessEngine.Redact(text, secrets);
        }

        // Раньше на каждый короткий секрет строилось регулярное выражение: секунды на такой странице.
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    private static string JsonEncodedTextOf(string value) => System.Text.Json.JsonEncodedText.Encode(value).Value;

    [Theory]
    [InlineData("получено «tok_9f8e7d6c5b4a3…»", "получено «••••»")]
    [InlineData("значение \"Qw\\u002Brty\\u002699\"", "значение \"••••\"")]
    [InlineData("ввод tok_9f8e7d6c5b4a32...", "ввод ••••")]
    [InlineData("ПИН 1234, заказ A-12345", "ПИН ••••, заказ A-12345")]
    [InlineData("слово tok_… и ещё", "слово tok_… и ещё")]
    public void Shortened_and_escaped_previews_of_a_secret_are_hidden(string text, string expected) =>
        Assert.Equal(expected, ProcessEngine.Redact(text, ProcessEngine.Expand(["tok_9f8e7d6c5b4a3210fedcba", "Qw+rty&99", "1234"])));

    [Fact]
    public async Task Changed_secret_numbers_and_wrong_type_values_stay_hidden()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Смена")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("pin", VariableType.Number, secret: true)
            .Node("start", "start")
            .Node("log", "log", new { message = "вход: {{ vars.password }}" })
            .Node("set", "variable.set", new { variable = "password", value = "'second-Pa55'" })
            .Node("pin", "variable.set", new { variable = "pin", value = "987654" })
            .Node("logPin", "log", new { message = "ПИН {{ vars.pin }}" })
            .Node("wrong", "variable.set", new { variable = "pin", value = "'zz-wrong-pin'" })
            .Node("end", "end")
            .Chain("start", "log", "set", "pin", "logPin", "wrong", "end").Build();
        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "S3cr3t-Pa55" });
        var stopped = await h.WaitForAsync(id, p => p.Status is ProcessStatus.NeedsAttention or ProcessStatus.Failed);

        // Значение неверного типа — в сообщении об ошибке, но скрыто: оно присваивалось секретной переменной.
        Assert.DoesNotContain("zz-wrong-pin", stopped.StatusMessage);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message));
        Assert.Contains("вход: ••••", journal);
        Assert.Contains("ПИН ••••", journal);
        Assert.DoesNotContain("987654", journal);
        Assert.DoesNotContain("zz-wrong-pin", journal);

        // При показе: прежнее значение пароля в параметрах шага скрыто, хотя переменная уже другая; число — тоже.
        var masker = new SecretMasker(stopped, scenario);
        var steps = (await h.StepsAsync(id)).Select(masker.Step).ToList();
        Assert.Equal(ProcessEngine.SecretMask, steps.Single(s => s.NodeId == "log").Input!["message"]!.GetValue<string>());
        Assert.Equal(ProcessEngine.SecretMask, steps.Single(s => s.NodeId == "set").Output!["variables"]!["password"]!.GetValue<string>());
        Assert.Equal(ProcessEngine.SecretMask, steps.Single(s => s.NodeId == "pin").Output!["variables"]!["pin"]!.GetValue<string>());
        var shown = string.Join("\n", steps.Select(s => $"{s.Input?.ToJsonString()} {s.Output?.ToJsonString()} {s.Error} {s.Resolution}"));
        Assert.DoesNotContain("S3cr3t-Pa55", shown);
        Assert.DoesNotContain("second-Pa55", shown);
        Assert.DoesNotContain("987654", shown);
        await h.Engine.CancelAsync(id);
    }
}
