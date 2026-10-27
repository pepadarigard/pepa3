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
using Pepa.Adapters.Browser;
using Pepa.Adapters.FileSystem;
using Pepa.Adapters.Http;
using Pepa.Adapters.Desktop;
using Pepa.Adapters.Telegram;
using Pepa.Core.Expressions;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using Pepa.Engine;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Host.Api;
using Pepa.Host.Logging;
using Pepa.Host.Plugins;
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
        var adsPower = new AdsPowerClient(() => new AdsPowerSettings
        {
            BaseUrl = settings.Current.AdsPowerUrl,
            ApiKey = settings.Current.AdsPowerApiKey,
        });
        var browsers = new BrowserSessions(new BrowserModuleOptions
        {
            DataDirectory = dataDirectory,
            ExecutablePathOverride = options.BrowserExecutablePath,
        }, adsPower);
        // Входящие сообщения Telegram становятся событиями движка (движок создаётся позже, поэтому ссылка — отложенная).
        ProcessEngine? engineRef = null;
        var telegram = new TelegramService(dataDirectory, input => engineRef!.PublishEventAsync(input))
        {
            ServerAddress = options.TelegramServerAddress,
            ConnectTimeout = options.TelegramConnectTimeout,
        };
        var desktop = options.Desktop ?? new UnavailableDesktop(
            "Окна Windows, мышь и клавиатура доступны только в приложении Pepa для Windows (Pepa.exe), а сейчас Pepa запущена без окна.");
        var registry = new NodeRegistry().RegisterLogicNodes().RegisterFlowNodes().RegisterFileNodes(workspace, () => settings.Current.FolderWatches)
            .RegisterHttpNodes().RegisterBrowserNodes(browsers, workspace).RegisterTelegramNodes(telegram, workspace).RegisterDesktopNodes(desktop);
        // Расширения — до движка и помощника: их блоки должны быть в реестре с самого начала (и в каталоге помощника).
        var plugins = PluginLoader.Load(options.PluginsDirectory ?? Path.Combine(dataDirectory, "plugins"), dataDirectory, registry,
            () => settings.Current.WorkspaceRoot, path => workspace.Resolve(path), elevated: OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess);
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
        builder.Services.AddSingleton(plugins);
        builder.Services.AddSingleton<IWorkspace>(workspace);
        builder.Services.AddSingleton<IExpressionEvaluator, JintExpressionEvaluator>();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(hostContext);
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton(adsPower);
        builder.Services.AddSingleton(browsers);
        var recorder = new BrowserRecorder(browsers);
        builder.Services.AddSingleton(recorder);
        builder.Services.AddSingleton(telegram);
        builder.Services.AddSingleton(desktop);
        var desktopRecorder = new DesktopRecorder(options.DesktopInput ?? new UnavailableInputMonitor(
            "Запись действий в окнах Windows доступна только в приложении Pepa для Windows (Pepa.exe), а сейчас Pepa запущена без окна."));
        builder.Services.AddSingleton(desktopRecorder);
        builder.Services.AddSingleton(sp => new Notifications.AttentionNotifier((id, ct) => store.GetProcessAsync(id, ct), telegram,
            () => settings.Current.NotifyTelegram, options.DesktopAlerts, sp.GetRequiredService<ILogger<Notifications.AttentionNotifier>>()));
        builder.Services.AddSingleton(sp => new Backups.HistoryRetention(store, () => settings.Current.HistoryDays, TimeProvider.System,
            sp.GetRequiredService<ILogger<Backups.HistoryRetention>>()));
        builder.Services.AddSingleton(sp => new Backups.DatabaseBackups(store, TimeProvider.System, sp.GetRequiredService<ILogger<Backups.DatabaseBackups>>()));
        // Новые файлы в отслеживаемых папках — события движка.
        builder.Services.AddSingleton(sp => new FolderWatcher(workspace, Path.Combine(dataDirectory, "watches"),
            (input, ct) => sp.GetRequiredService<ProcessEngine>().PublishEventAsync(input, ct), TimeProvider.System,
            sp.GetRequiredService<ILogger<FolderWatcher>>(), options.FolderWatch));
        builder.Services.AddSingleton(sp => new ProcessEngine(
            sp.GetRequiredService<IPepaStore>(),
            sp.GetRequiredService<NodeRegistry>(),
            sp.GetRequiredService<IExpressionEvaluator>(),
            settings.Applied.ToEngineOptions(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<ProcessEngine>>()));
        builder.Services.AddSingleton(sp => new ScenarioService(store, sp.GetRequiredService<ProcessEngine>().Validator));
        builder.Services.AddSingleton(new AssistantKeyStore(store));
        // Помощнику — каталог блоков из того же реестра и два примера сценариев (как образец формата и стиля).
        builder.Services.AddSingleton(sp => new Assistant.ScenarioAssistant(registry, sp.GetRequiredService<ProcessEngine>().Validator,
            [SampleScenarios.TelegramReply(), SampleScenarios.AdsPowerTelegram()])
        {
            BaseUrl = options.AssistantBaseUrl,
        });
        builder.Services.AddSingleton<ProcessMapper>();

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<PepaHost>>();
        foreach (var plugin in plugins.Plugins)
        {
            if (plugin.Error is null)
            {
                logger.LogInformation("Расширение «{Name}» ({Id}) загружено: блоков — {Count}.", plugin.Name, plugin.Id, plugin.NodeTypes.Count);
            }
            else
            {
                logger.LogWarning("Расширение {Id} не загружено: {Error}", plugin.Id, plugin.Error);
            }
        }
        var engine = app.Services.GetRequiredService<ProcessEngine>();
        engineRef = engine;
        var events = app.Services.GetRequiredService<EventHub>();
        telegram.Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<TelegramService>();
        telegram.StatusChanged += () => events.Publish("integrations");
        recorder.Changed += () => events.Publish("recorder");
        desktopRecorder.Changed += () => events.Publish("recorder");
        engine.Changed += change => events.Publish("process", change.ProcessId);
        var notifier = app.Services.GetRequiredService<Notifications.AttentionNotifier>();
        engine.Changed += change => notifier.OnProcessChanged(change.ProcessId);
        engine.EventsChanged += () => events.Publish("events");
        engine.TriggersChanged += () => events.Publish("triggers");
        // Процесс завершился — закрыть его браузеры (и профили AdsPower, если так настроено в блоке).
        engine.ProcessFinished += processId => _ = browsers.ReleaseProcessAsync(processId);

        // Пока Telegram после запуска догружает пропущенные сообщения, тайм-ауты ожиданий ответа не наступают.
        if (telegram.WillCatchUp)
        {
            engine.SourceCatchingUp(TelegramEvents.Source);
            telegram.CaughtUp += () => engine.SourceCaughtUp(TelegramEvents.Source);
        }

        // То же для файлов, появившихся в отслеживаемых папках, пока Pepa была закрыта.
        var watcher = app.Services.GetRequiredService<FolderWatcher>();
        watcher.Configure(settings.Current.FolderWatches);
        watcher.Changed += () => events.Publish("watches");
        if (watcher.WillCatchUp)
        {
            engine.SourceCatchingUp(FileEvents.Source);
            watcher.CaughtUp += () => engine.SourceCaughtUp(FileEvents.Source);
        }

        engine.RestoredFromBackupAt = store.RestoredFromBackupAt;
        hostContext.Recovery = await engine.StartAsync(cancellationToken);
        if (store.RestoredFromBackupAt is { } restoredAt)
        {
            // Процессы уже ждут решения пользователя — отметка «база из копии» больше не нужна.
            await store.ClearRestoreMarkerAsync(cancellationToken);
            logger.LogWarning("База восстановлена из резервной копии от {At}. {Recovery}", restoredAt, hostContext.Recovery);
        }

        var unfinished = await store.ListProcessesAsync(new ProcessQuery
        {
            Statuses = [ProcessStatus.Running, ProcessStatus.Waiting, ProcessStatus.Paused, ProcessStatus.NeedsAttention],
            Limit = int.MaxValue,
        }, cancellationToken);
        browsers.Prune(unfinished.Select(p => p.Id).ToList());
        // Уже ждавшие решения до запуска — не новость (их видно на «Главной»); после перезапуска — одно общее сообщение.
        notifier.Start(unfinished.Where(p => p.Status == ProcessStatus.NeedsAttention), hostContext.Recovery);
        // Подключение к уже авторизованному Telegram — в фоне, чтобы не задерживать запуск окна.
        _ = Task.Run(async () =>
        {
            try
            {
                await telegram.StartAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось подключиться к Telegram при запуске.");
            }
        });
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
        // Ежедневная резервная копия базы — в фоне, запуск окна она не задерживает.
        app.Services.GetRequiredService<Backups.DatabaseBackups>().Start();
        app.Services.GetRequiredService<Backups.HistoryRetention>().Start();
        watcher.Start(engine.StartedAt);
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
        // Набор примеров версионирован: после обновления Pepa добавляются только новые, удалённые пользователем не возвращаются.
        var settings = await store.GetSettingsAsync();
        var version = settings.TryGetValue("samples.version", out var text) && int.TryParse(text, out var parsed)
            ? parsed
            : settings.ContainsKey("samples.seeded") ? 1 : 0;
        if (version >= SampleScenarios.CurrentVersion)
        {
            return;
        }

        // Пример с таким же названием уже есть (например, сбой между созданием примеров и записью версии) — не дублировать.
        var existing = (await store.ListScenariosAsync()).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        // В обратном порядке: список сценариев отсортирован по времени изменения, меньший номер окажется выше.
        var added = 0;
        foreach (var (_, sample) in SampleScenarios.Versioned().Where(s => s.Version > version).Reverse())
        {
            if (existing.Contains(sample.Name))
            {
                continue;
            }

            await scenarios.CreateAsync(sample);
            added++;
        }

        await using var write = await store.BeginWriteAsync();
        await write.SetSettingAsync("samples.seeded", DateTimeOffset.UtcNow.ToString("O"));
        await write.SetSettingAsync("samples.version", SampleScenarios.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await write.CommitAsync();
        logger.LogInformation("Добавлены примеры сценариев: {Count}.", added);
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
        var telegram = Services.GetRequiredService<TelegramService>();
        try
        {
            // Сначала прервать ожидание ответа Telegram — иначе запрос входа задержал бы остановку сервера.
            telegram.BeginShutdown();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
        }
        finally
        {
            // Слежение за папками — до движка: события после остановки движка не принимаются.
            await Services.GetRequiredService<FolderWatcher>().DisposeAsync();
            // Сначала движок: он дожидается выполняющихся шагов (отправка в Telegram должна успеть завершиться), потом Telegram.
            await engine.StopAsync();
            await telegram.DisposeAsync();
            await Services.GetRequiredService<BrowserRecorder>().DisposeAsync();
            await Services.GetRequiredService<Backups.DatabaseBackups>().DisposeAsync();
            await Services.GetRequiredService<Backups.HistoryRetention>().DisposeAsync();
            await Services.GetRequiredService<Notifications.AttentionNotifier>().DisposeAsync();
            if (Services.GetRequiredService<DesktopRecorder>().State.Recording)
            {
                // Перехват мыши и клавиатуры не должен пережить Pepa.
                await Services.GetRequiredService<DesktopRecorder>().StopAsync(CancellationToken.None);
            }

            await Services.GetRequiredService<BrowserSessions>().DisposeAsync();
            Services.GetRequiredService<AdsPowerClient>().Dispose();
            await events.DisposeAsync();
            await _app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            _lock?.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
