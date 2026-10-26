using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core.Serialization;

namespace Pepa.Host.Tests;

/// <summary>Настоящий экземпляр Pepa на случайном порту во временной папке.</summary>
public sealed class HostFixture : IAsyncDisposable
{
    public const string Token = "test-token";

    private HostFixture(string root, PepaHost host)
    {
        Root = root;
        Host = host;
        Client = CreateClient(host);
    }

    public string Root { get; }
    public string DataDirectory => Path.Combine(Root, "data");
    public string Workspace => Path.Combine(Root, "workspace");
    public PepaHost Host { get; private set; }
    public HttpClient Client { get; private set; }

    public static async Task<HostFixture> StartAsync(bool seedSamples = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "pepa-host-tests", Guid.NewGuid().ToString("N"));
        var host = await PepaHost.StartAsync(Options(root, seedSamples));
        return new HostFixture(root, host);
    }

    public static PepaHostOptions Options(string root, bool seedSamples = true) => new()
    {
        DataDirectory = Path.Combine(root, "data"),
        DefaultWorkspaceRoot = Path.Combine(root, "workspace"),
        Token = Token,
        UiDirectory = Path.Combine(root, "no-ui"),
        SeedSamples = seedSamples,
        // Проверки никогда не обращаются к настоящему Telegram: порт 1 на этой машине закрыт.
        TelegramServerAddress = "127.0.0.1:1",
        TelegramConnectTimeout = TimeSpan.FromSeconds(10),
    };

    public async Task RestartAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        Host = await PepaHost.StartAsync(Options(Root));
        Client = CreateClient(Host);
    }

    private static HttpClient CreateClient(PepaHost host)
    {
        var client = new HttpClient { BaseAddress = host.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-Pepa-Token", Token);
        return client;
    }

    public async Task<JsonNode> GetAsync(string path)
    {
        var response = await Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    public async Task<(int Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: PepaJson.Options);
        }

        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public async Task<JsonNode> WaitForProcessAsync(string id, Func<JsonNode, bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var details = await GetAsync($"/api/processes/{id}");
            if (condition(details["process"]!))
            {
                return details;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Процесс не дошёл до нужного состояния: {details["process"]!.ToJsonString()}");
            }

            await Task.Delay(50);
        }
    }

    public async Task<string> ScenarioIdAsync(string namePrefix)
    {
        var list = (await GetAsync("/api/scenarios")).AsArray();
        return list.First(s => s!["name"]!.GetValue<string>().StartsWith(namePrefix, StringComparison.Ordinal))!["id"]!
            .GetValue<string>();
    }

    public static string Status(JsonNode process) => process["status"]!.GetValue<string>();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static readonly JsonSerializerOptions Json = PepaJson.Options;
}
