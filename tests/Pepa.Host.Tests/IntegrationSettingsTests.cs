using Microsoft.Extensions.DependencyInjection;
using Pepa.Engine.Persistence;

namespace Pepa.Host.Tests;

/// <summary>Настройки AdsPower, проверка связи и обновление набора примеров.</summary>
public sealed class IntegrationSettingsTests
{
    [Fact]
    public async Task AdsPower_settings_are_saved_and_key_is_never_returned()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var settings = await f.GetAsync("/api/settings");
        Assert.Equal("http://127.0.0.1:50325", settings["adsPowerUrl"]!.GetValue<string>());
        Assert.False(settings["adsPowerHasKey"]!.GetValue<bool>());

        settings["adsPowerUrl"] = "http://127.0.0.1:1/";
        settings["adsPowerApiKey"] = "secret-key";
        var (status, saved) = await f.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(200, status);
        Assert.Equal("http://127.0.0.1:1", saved!["adsPowerUrl"]!.GetValue<string>());
        Assert.True(saved["adsPowerHasKey"]!.GetValue<bool>());
        Assert.Null(saved["adsPowerApiKey"]);
        Assert.DoesNotContain("secret-key", (await f.GetAsync("/api/settings")).ToJsonString());

        // Пустое значение ключа в запросе — не трогать сохранённый; пустая строка — удалить.
        saved["adsPowerApiKey"] = null;
        Assert.True((await f.SendAsync(HttpMethod.Put, "/api/settings", saved)).Body!["adsPowerHasKey"]!.GetValue<bool>());
        saved["adsPowerApiKey"] = "";
        Assert.False((await f.SendAsync(HttpMethod.Put, "/api/settings", saved)).Body!["adsPowerHasKey"]!.GetValue<bool>());

        saved["adsPowerUrl"] = "ftp://example";
        Assert.Equal(400, (await f.SendAsync(HttpMethod.Put, "/api/settings", saved)).Status);
    }

    [Fact]
    public async Task AdsPower_check_explains_missing_connection()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var settings = await f.GetAsync("/api/settings");
        settings["adsPowerUrl"] = "http://127.0.0.1:1";
        await f.SendAsync(HttpMethod.Put, "/api/settings", settings);

        var (status, check) = await f.SendAsync(HttpMethod.Post, "/api/integrations/adspower/check");
        Assert.Equal(200, status);
        Assert.False(check!["ok"]!.GetValue<bool>());
        Assert.Contains("Нет связи с AdsPower", check["message"]!.GetValue<string>());

        var (profilesStatus, error) = await f.SendAsync(HttpMethod.Get, "/api/integrations/adspower/profiles");
        Assert.Equal(502, profilesStatus);
        Assert.Contains("Local API", error!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Browser_blocks_are_described_for_the_constructor()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var types = await f.GetAsync("/api/node-types");
        var all = types["nodeTypes"]!.AsArray();
        var ads = all.Single(t => t!["type"]!.GetValue<string>() == "adspower.open")!;
        Assert.Equal("browser", ads["category"]!.GetValue<string>());
        Assert.NotNull(ads["resourceHint"]);
        Assert.Equal("adspower-profiles", ads["fields"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "profileId")!["lookup"]!.GetValue<string>());
        Assert.Contains(types["categories"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "browser");
        foreach (var type in new[] { "browser.open", "browser.goto", "browser.click", "browser.fill", "browser.press", "browser.waitFor", "browser.read", "browser.screenshot", "browser.close" })
        {
            Assert.Contains(all, t => t!["type"]!.GetValue<string>() == type);
        }
    }

    [Fact]
    public async Task Updating_Pepa_adds_only_new_samples()
    {
        await using var f = await HostFixture.StartAsync();
        var all = (await f.GetAsync("/api/scenarios")).AsArray();
        Assert.Equal(Samples.SampleScenarios.All().Count, all.Count);

        // Как будто установлена версия Pepa с набором примеров 2: пользователь удалил «Пример 1».
        var first = all.Single(s => s!["name"]!.GetValue<string>().StartsWith("Пример 1", StringComparison.Ordinal))!["id"]!.GetValue<string>();
        await f.SendAsync(HttpMethod.Delete, $"/api/scenarios/{first}");
        foreach (var name in new[] { "Пример 7", "Пример 8" })
        {
            var id = all.Single(s => s!["name"]!.GetValue<string>().StartsWith(name, StringComparison.Ordinal))!["id"]!.GetValue<string>();
            await f.SendAsync(HttpMethod.Delete, $"/api/scenarios/{id}");
        }

        var store = f.Host.Services.GetRequiredService<IPepaStore>();
        await using (var write = await store.BeginWriteAsync())
        {
            await write.SetSettingAsync("samples.version", "2");
            await write.CommitAsync();
        }

        await f.RestartAsync();
        var names = (await f.GetAsync("/api/scenarios")).AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Contains(names, n => n.StartsWith("Пример 7", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("Пример 8", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Пример 1", StringComparison.Ordinal));
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
