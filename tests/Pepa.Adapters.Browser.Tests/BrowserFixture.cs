using Pepa.Adapters.FileSystem;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>Движок с блоками браузера, тестовым сайтом и (по желанию) имитацией AdsPower.</summary>
internal sealed class BrowserFixture : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pepa-browser-tests", Guid.NewGuid().ToString("N"));
    private AdsPowerClient? _client;

    private BrowserFixture()
    {
        Directory.CreateDirectory(Workspace);
    }

    public EngineHarness H { get; private set; } = null!;
    public BrowserSessions Sessions { get; private set; } = null!;
    public TestSite Site { get; } = new();
    public FakeAdsPower? Ads { get; private set; }
    public TestSnapshots Snapshots { get; } = new();
    public string Workspace => Path.Combine(_root, "workspace");
    public string DataDirectory => Path.Combine(_root, "data");

    public static async Task<BrowserFixture> CreateAsync(bool withAdsPower = false, string? apiKey = null)
    {
        var fixture = new BrowserFixture();
        if (withAdsPower)
        {
            fixture.Ads = new FakeAdsPower(TestBrowser.ExecutableForCdp!, apiKey);
        }

        fixture.H = await EngineHarness.CreateAsync(extraNodes: registry =>
            registry.RegisterBrowserNodes(fixture.NewSessions(apiKey), new Workspace(() => fixture.Workspace)));
        fixture.H.WaitTimeout = TimeSpan.FromSeconds(90);
        fixture.Subscribe();
        return fixture;
    }

    private BrowserSessions NewSessions(string? apiKey)
    {
        _client?.Dispose();
        _client = new AdsPowerClient(() => new AdsPowerSettings
        {
            BaseUrl = Ads?.BaseUrl ?? "http://127.0.0.1:1",
            ApiKey = apiKey,
            MinInterval = TimeSpan.Zero,
        });
        Sessions = new BrowserSessions(new BrowserModuleOptions
        {
            DataDirectory = DataDirectory,
            ExecutablePathOverride = TestBrowser.ExecutablePath,
            Snapshots = Snapshots,
        }, _client);
        return Sessions;
    }

    private void Subscribe() => H.Engine.ProcessFinished += id => _ = Sessions.ReleaseProcessAsync(id);

    /// <summary>Закрыть Pepa (браузеры, запущенные ею, закрываются; профили AdsPower остаются открытыми) и открыть снова.</summary>
    public async Task RestartAsync()
    {
        await H.Engine.StopAsync();
        await Sessions.DisposeAsync();
        await H.RestartAsync();
        Subscribe();
    }

    /// <summary>Настройки браузера в блоке «Открыть браузер» для этой машины.</summary>
    public static object OpenConfig(string? url = null, bool keepLogin = false) => new
    {
        browser = TestBrowser.Channel == "chrome" ? "chrome" : "msedge",
        headless = true,
        keepLogin,
        url,
    };

    public async ValueTask DisposeAsync()
    {
        await H.DisposeAsync();
        await Sessions.DisposeAsync();
        Ads?.Dispose();
        Site.Dispose();
        _client?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Снимки при ошибках — в памяти теста.</summary>
internal sealed class TestSnapshots : Pepa.Core.Nodes.ISnapshotStore
{
    public System.Collections.Concurrent.ConcurrentQueue<(string ProcessId, byte[] Image, string Name)> Saved { get; } = new();

    public ValueTask<string?> SaveAsync(string processId, byte[] image, string extension, CancellationToken cancellationToken)
    {
        var name = $"{Saved.Count + 1:000}{extension}";
        Saved.Enqueue((processId, image, name));
        return ValueTask.FromResult<string?>(name);
    }
}
