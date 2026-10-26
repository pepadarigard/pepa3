using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Pepa.Adapters.FileSystem;
using Pepa.Core.Expressions;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using Pepa.Engine;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Host.Api;
using Pepa.Host.Logging;
using Pepa.Host.Samples;
using Pepa.Host.Security;
using Pepa.Host.Settings;
using Pepa.Storage.Sqlite;

namespace Pepa.Host;

/// <summary>
/// Запущенный экземпляр платформы: хранилище, движок, адаптеры и локальный HTTP API с интерфейсом.
/// Один и тот же код используют оконное приложение и сервер без окна.
/// </summary>
public sealed class PepaHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly InstanceLock? _lock;
    private int _stopped;

    private PepaHost(WebApplication app, InstanceLock? instanceLock, Uri baseUrl, string token, string dataDirectory)
    {
        _app = app;
        _lock = instanceLock;
        BaseUrl = baseUrl;
        Token = token;
        DataDirectory = dataDirectory;
    }

    public Uri BaseUrl { get; }
    public string DataDirectory { get; }
    public string Token { get; }

    /// <summary>Адрес для открытия интерфейса: токен передаётся во фрагменте (#), который не уходит на сервер.</summary>
    public Uri LaunchUrl => new($"{BaseUrl.ToString().TrimEnd('/')}/#token={Uri.EscapeDataString(Token)}");

    public ProcessEngine Engine => _app.Services.GetRequiredService<ProcessEngine>();

    /// <summary>Срабатывает, когда приложение просят завершиться (Ctrl+C, SIGTERM, остановка службы).</summary>
    public CancellationToken ShutdownRequested => _app.Lifetime.ApplicationStopping;
    public IServiceProvider Services => _app.Services;

    public static async Task<PepaHost> StartAsync(PepaHostOptions options, CancellationToken cancellationToken = default)
    {
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var instanceLock = options.UseInstanceLock ? InstanceLock.Acquire(dataDirectory) : null;
        try
        {
            return await StartCoreAsync(options, dataDirectory, instanceLock, cancellationToken);
        }
        catch
        {
            instanceLock?.Dispose();
            throw;
        }
    }

    private static async Task<PepaHost> StartCoreAsync(PepaHostOptions options, string dataDirectory,
        InstanceLock? instanceLock, CancellationToken cancellationToken)
    {
        var store = new SqliteStore(Path.Combine(dataDirectory, "pepa.db"));
        await store.InitializeAsync(cancellationToken);
        var settings = await SettingsService.LoadAsync(store, dataDirectory,
            options.DefaultWorkspaceRoot ?? HostPaths.DefaultWorkspace(dataDirectory));
        var workspace = new Workspace(() => settings.Current.WorkspaceRoot);
        var registry = new NodeRegistry().RegisterLogicNodes().RegisterFlowNodes().RegisterFileNodes(workspace);
        var token = options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hostContext = new HostContext
        {
            Token = token,
            DataDirectory = dataDirectory,
            IsDesktop = options.IsDesktop,
            StartedAt = DateTimeOffset.Now,
            Port = options.Port,
        };

        var uiDirectory = options.UiDirectory ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(PepaHost).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataDirectory, "logs")));
        if (options.ConsoleLogging)
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        }

        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            var source = PepaJson.Options;
            json.SerializerOptions.PropertyNamingPolicy = source.PropertyNamingPolicy;
            json.SerializerOptions.Encoder = source.Encoder;
            foreach (var converter in source.Converters)
            {
                json.SerializerOptions.Converters.Add(converter);
            }
        });
        builder.Services.AddSingleton<IPepaStore>(store);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IWorkspace>(workspace);
        builder.Services.AddSingleton<IExpressionEvaluator, JintExpressionEvaluator>();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(hostContext);
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton(sp => new ProcessEngine(
            sp.GetRequiredService<IPepaStore>(),
            sp.GetRequiredService<NodeRegistry>(),
            sp.GetRequiredService<IExpressionEvaluator>(),
            settings.Applied.ToEngineOptions(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<ProcessEngine>>()));
        builder.Services.AddSingleton(sp => new ScenarioService(store, sp.GetRequiredService<ProcessEngine>().Validator));
        builder.Services.AddSingleton<ProcessMapper>();

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<PepaHost>>();
        var engine = app.Services.GetRequiredService<ProcessEngine>();
        var events = app.Services.GetRequiredService<EventHub>();
        engine.Changed += change => events.Publish("process", change.ProcessId);

        hostContext.Recovery = await engine.StartAsync(cancellationToken);
        if (options.SeedSamples)
        {
            await SeedSamplesAsync(store, app.Services.GetRequiredService<ScenarioService>(), logger);
        }

        app.UseMiddleware<LocalAccessMiddleware>();
        app.Use((http, next) => http.Request.Path.StartsWithSegments("/api")
            ? ApiEndpoints.HandleErrorsAsync(http, () => next(http))
            : next(http));
        app.MapPepaApi();
        app.Map("/api/{**rest}", () => Results.NotFound(new ErrorDto("Такого адреса API нет.")));
        MapInterface(app, uiDirectory);

        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var baseUrl = new Uri(address.Replace("[::1]", "127.0.0.1"));
        hostContext.Port = baseUrl.Port;
        logger.LogInformation("Pepa {Version} запущена: {Url}. Данные: {Data}. {Recovery}",
            HostContext.Version, baseUrl, dataDirectory, hostContext.Recovery);
        return new PepaHost(app, instanceLock, baseUrl, token, dataDirectory);
    }

    private static void MapInterface(WebApplication app, string uiDirectory)
    {
        if (!File.Exists(Path.Combine(uiDirectory, "index.html")))
        {
            app.MapFallback(() => Results.Content(
                """
                <!doctype html><html lang="ru"><meta charset="utf-8"><title>Pepa</title>
                <body style="font-family:system-ui;padding:40px;max-width:640px">
                <h1>Интерфейс не собран</h1>
                <p>Движок и API работают, но файлы интерфейса не найдены. Соберите их:</p>
                <pre>cd ui
                npm ci
                npm run build</pre>
                </body></html>
                """, "text/html; charset=utf-8"));
            return;
        }

        var files = new PhysicalFileProvider(uiDirectory);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                // Файлы с хешем в имени можно кешировать надолго, index.html — нет.
                context.Context.Response.Headers.CacheControl =
                    context.File.Name == "index.html" ? "no-cache" : "public, max-age=31536000, immutable";
            },
        });
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
    }

    private static async Task SeedSamplesAsync(IPepaStore store, ScenarioService scenarios, ILogger logger)
    {
        var settings = await store.GetSettingsAsync();
        if (settings.ContainsKey("samples.seeded"))
        {
            return;
        }

        // В обратном порядке: список сценариев отсортирован по времени изменения, «Пример 1» окажется сверху.
        foreach (var sample in SampleScenarios.All().Reverse())
        {
            await scenarios.CreateAsync(sample);
        }

        await using var write = await store.BeginWriteAsync();
        await write.SetSettingAsync("samples.seeded", DateTimeOffset.UtcNow.ToString("O"));
        await write.CommitAsync();
        logger.LogInformation("Созданы примеры сценариев.");
    }

    /// <summary>Сколько процессов выполняется или ждёт (для предупреждения при закрытии окна).</summary>
    public async Task<int> CountActiveProcessesAsync()
    {
        var store = Services.GetRequiredService<IPepaStore>();
        var active = await store.ListProcessesAsync(new ProcessQuery
        {
            Statuses = [ProcessStatus.Running, ProcessStatus.Waiting],
            Limit = 10_000,
        });
        return active.Count;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        var engine = Engine;
        var events = Services.GetRequiredService<EventHub>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
        }
        finally
        {
            await engine.StopAsync();
            await events.DisposeAsync();
            await _app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            _lock?.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
