using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Host.Security;
using Pepa.Host.Settings;

namespace Pepa.Host.Api;

internal static class ApiEndpoints
{
    private static readonly ProcessStatus[] ActiveStatuses =
        [ProcessStatus.Running, ProcessStatus.Waiting, ProcessStatus.Paused, ProcessStatus.NeedsAttention];

    private static readonly ProcessStatus[] FinishedStatuses =
        [ProcessStatus.Completed, ProcessStatus.Failed, ProcessStatus.Cancelled];

    public static void MapPepaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // ───────────── Система ─────────────
        api.MapGet("/info", (HostContext host, SettingsService settings) => new InfoDto(
            HostContext.Version, host.DataDirectory, settings.Current.WorkspaceRoot, host.IsDesktop, host.StartedAt, host.Recovery));

        api.MapGet("/node-types", (ProcessEngine engine) =>
            new NodeTypesDto(engine.Registry.Categories, engine.Registry.Descriptors));

        api.MapGet("/settings", (SettingsService settings) => ToDto(settings));

        api.MapPut("/settings", async (SettingsDto request, SettingsService settings) =>
        {
            var current = settings.Current;
            await settings.SaveAsync(new PlatformSettings
            {
                WorkspaceRoot = request.WorkspaceRoot,
                MaxParallelProcesses = request.MaxParallelProcesses,
                StepBudget = request.StepBudget,
                ActionTimeoutSeconds = request.ActionTimeoutSeconds,
                AdsPowerUrl = request.AdsPowerUrl ?? current.AdsPowerUrl,
                AdsPowerApiKey = request.AdsPowerApiKey ?? current.AdsPowerApiKey,
            });
            return ToDto(settings);
        });

        // ───────────── Интеграции ─────────────
        api.MapPost("/integrations/adspower/check", async (Adapters.Browser.AdsPowerClient adsPower, SettingsService settings,
            CancellationToken ct) =>
        {
            try
            {
                await adsPower.CheckAsync(ct);
                var profiles = await adsPower.ListProfilesAsync(1, 1, ct);
                return new CheckResultDto(true, $"AdsPower на связи ({settings.Current.AdsPowerUrl})." +
                                                (profiles.Count == 0 ? " Профилей не найдено." : ""));
            }
            catch (Adapters.Browser.AdsPowerException ex)
            {
                return new CheckResultDto(false, ex.Message);
            }
        });

        api.MapGet("/integrations/telegram", (Adapters.Telegram.TelegramService telegram) => TelegramDto(telegram));

        api.MapPost("/integrations/telegram/login", async (TelegramLoginRequest request, Adapters.Telegram.TelegramService telegram) =>
        {
            try
            {
                await telegram.BeginLoginAsync(
                    new Adapters.Telegram.TelegramCredentials(request.ApiId, request.ApiHash ?? "", request.Phone ?? ""));
                return Results.Ok(TelegramDto(telegram));
            }
            catch (Adapters.Telegram.TelegramLoginException ex)
            {
                return Results.Json(new ErrorDto(ex.Message), Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new ErrorDto($"Не удалось связаться с Telegram: {ex.Message}"), Core.Serialization.PepaJson.Options,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapPost("/integrations/telegram/login/continue", async (TelegramCodeRequest request, Adapters.Telegram.TelegramService telegram) =>
        {
            try
            {
                await telegram.ContinueLoginAsync(request.Value ?? "");
                return Results.Ok(TelegramDto(telegram));
            }
            catch (Adapters.Telegram.TelegramLoginException ex)
            {
                return Results.Json(new ErrorDto(ex.Message), Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new ErrorDto($"Не удалось связаться с Telegram: {ex.Message}"), Core.Serialization.PepaJson.Options,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapPost("/integrations/telegram/logout", async (Adapters.Telegram.TelegramService telegram) =>
        {
            await telegram.LogoutAsync();
            return TelegramDto(telegram);
        });

        api.MapGet("/integrations/adspower/profiles", async (Adapters.Browser.AdsPowerClient adsPower, CancellationToken ct) =>
        {
            try
            {
                var profiles = await adsPower.ListProfilesAsync(1, 200, ct);
                return Results.Ok(profiles.Select(p => new AdsPowerProfileDto(p.Id, p.Number, p.Name, p.Group)).ToList());
            }
            catch (Adapters.Browser.AdsPowerException ex)
            {
                return Results.Json(new ErrorDto(ex.Message), Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapGet("/integrations/desktop/windows", async (Adapters.Desktop.IDesktop desktop, CancellationToken ct) =>
        {
            if (desktop.UnavailableReason is { } reason)
            {
                return Results.Json(new ErrorDto(reason), Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                var windows = await desktop.ListWindowsAsync(ct);
                return Results.Ok(windows.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title)
                    .Select(w => new DesktopWindowDto(w.Title, w.ProcessName)).ToList());
            }
            catch (Adapters.Desktop.DesktopException ex)
            {
                return Results.Json(new ErrorDto($"Windows не дал список окон: {ex.Message}"), Core.Serialization.PepaJson.Options,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapGet("/overview", async (IPepaStore store, ProcessMapper mapper, CancellationToken ct) =>
        {
            var active = await store.ListProcessesAsync(new ProcessQuery { Statuses = ActiveStatuses, Limit = 500 }, ct);
            var today = DateTimeOffset.Now.Date;
            var startOfDay = new DateTimeOffset(today, TimeZoneInfo.Local.GetUtcOffset(today));
            var finishedToday = await store.ListProcessesAsync(
                new ProcessQuery { Statuses = FinishedStatuses, CreatedAfter = startOfDay, Limit = 10_000 }, ct);
            var recent = await store.ListProcessesAsync(new ProcessQuery { Statuses = FinishedStatuses, Limit = 8 }, ct);
            var activeDtos = await mapper.SummariesAsync(active, ct);
            var conflicts = await store.ListEventsAsync(new EventQuery { States = [EventState.Conflict], Limit = 1000 }, ct);
            var triggers = await mapper.TriggersAsync(await store.ListTriggersAsync(null, ct), ct);
            return new OverviewDto(
                new OverviewCountsDto(
                    active.Count(p => p.Status == ProcessStatus.Running),
                    active.Count(p => p.Status == ProcessStatus.Waiting),
                    active.Count(p => p.Status == ProcessStatus.Paused),
                    active.Count(p => p.Status == ProcessStatus.NeedsAttention),
                    finishedToday.Count(p => p.Status == ProcessStatus.Completed),
                    finishedToday.Count(p => p.Status is ProcessStatus.Failed or ProcessStatus.Cancelled)),
                activeDtos.Where(p => p.Status == ProcessStatus.NeedsAttention).ToList(),
                activeDtos.Where(p => p.Status != ProcessStatus.NeedsAttention).ToList(),
                await mapper.SummariesAsync(recent, ct),
                conflicts.Count,
                triggers.Where(t => t.MissedRuns > 0).ToList(),
                triggers.Where(t => t is { Enabled: true, NextRunAt: not null }).OrderBy(t => t.NextRunAt).Take(5).ToList());
        });

        // ───────────── Сценарии ─────────────
        api.MapGet("/scenarios", async (ScenarioService scenarios, IPepaStore store, CancellationToken ct) =>
        {
            var list = await scenarios.ListAsync(ct);
            var latest = await store.GetLatestProcessPerScenarioAsync(ct);
            return list.Select(s =>
            {
                var definition = ScenarioService.Parse(s);
                var report = scenarios.Validate(definition);
                var last = latest.GetValueOrDefault(s.Id);
                return new ScenarioSummaryDto(s.Id, s.Name, s.Description, s.Version, s.CreatedAt, s.UpdatedAt,
                    definition.Nodes.Count, report.ErrorCount, report.WarningCount,
                    definition.Variables.Any(v => v.IsInput),
                    last is null ? null : new ProcessBriefDto(last.Id, last.Status, last.CreatedAt, last.FinishedAt));
            }).ToList();
        });

        api.MapGet("/scenarios/{id}", async (string id, ScenarioService scenarios, CancellationToken ct) =>
            await scenarios.GetAsync(id, ct) is { } stored ? Results.Ok(ToDto(stored, scenarios)) : NotFound("Сценарий не найден."));

        // ───────────── Запись действий в браузере ─────────────
        api.MapGet("/recorder", (Adapters.Browser.BrowserRecorder recorder) => RecorderDto(recorder.State));

        api.MapPost("/recorder/start", async (RecorderStartRequest request, Adapters.Browser.BrowserRecorder recorder, IPepaStore store,
            CancellationToken ct) =>
        {
            var adsPower = string.Equals(request.Browser, "adspower", StringComparison.OrdinalIgnoreCase);
            var profileId = request.ProfileId?.Trim();
            // Профиль, который сейчас занят процессом, записи не отдаётся: два «пользователя» в одном браузере мешали бы друг другу.
            var resourceKey = adsPower ? Adapters.Browser.BrowserSessions.AdsPowerResourceKey(profileId ?? "") : "pepa-browser:main";
            if ((await store.ListResourcesAsync(ct)).FirstOrDefault(r => r.Key == resourceKey && r.HolderProcessId is not null) is { } held)
            {
                return Results.Json(new ErrorDto($"{held.Title} сейчас занят процессом #{held.HolderProcessId![^6..]} — дождитесь его окончания или остановите."),
                    Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                await recorder.StartAsync(new Adapters.Browser.RecorderStart(
                    adsPower ? Adapters.Browser.RecorderBrowser.AdsPower : Adapters.Browser.RecorderBrowser.Pepa,
                    profileId, string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim()), ct);
                return Results.Ok(RecorderDto(recorder.State));
            }
            catch (Adapters.Browser.RecorderException ex)
            {
                return Results.Json(new ErrorDto(ex.Message), Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Microsoft.Playwright.PlaywrightException ex)
            {
                return Results.Json(new ErrorDto($"Не удалось открыть страницу: {Adapters.Browser.BrowserSessions.FirstLine(ex.Message)}"),
                    Core.Serialization.PepaJson.Options, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapPost("/recorder/stop", async (Adapters.Browser.BrowserRecorder recorder, CancellationToken ct) =>
        {
            await recorder.StopAsync(ct);
            return RecorderDto(recorder.State);
        });

        // ИИ-помощник: ключ API и черновик блоков по описанию.
        api.MapGet("/assistant", async (AssistantKeyStore keys, Assistant.ScenarioAssistant assistant, CancellationToken ct) =>
            AssistantDto(await keys.GetAsync(ct), assistant));

        api.MapPut("/assistant/key", async (AssistantKeyRequest request, AssistantKeyStore keys, Assistant.ScenarioAssistant assistant,
            CancellationToken ct) =>
        {
            await keys.SetAsync(request.ApiKey ?? "", ct);
            return AssistantDto(await keys.GetAsync(ct), assistant);
        });

        api.MapDelete("/assistant/key", async (AssistantKeyStore keys, Assistant.ScenarioAssistant assistant, CancellationToken ct) =>
        {
            await keys.ClearAsync(ct);
            return AssistantDto(null, assistant);
        });

        api.MapPost("/assistant/check", async (AssistantKeyStore keys, Assistant.ScenarioAssistant assistant, CancellationToken ct) =>
        {
            try
            {
                var model = await assistant.CheckKeyAsync(await keys.GetAsync(ct) ?? "", ct);
                return new CheckResultDto(true, $"Ключ подходит, модель «{model}» доступна.");
            }
            catch (Assistant.AssistantException ex)
            {
                return new CheckResultDto(false, ex.Message);
            }
        });

        api.MapPost("/assistant/draft", async (AssistantDraftRequest request, AssistantKeyStore keys, Assistant.ScenarioAssistant assistant,
            CancellationToken ct) =>
        {
            try
            {
                var draft = await assistant.DraftAsync(await keys.GetAsync(ct) ?? "", new Assistant.AssistantRequest
                {
                    Description = request.Description ?? "",
                    ExistingVariables = request.Variables ?? [],
                }, ct);
                return Results.Json(new AssistantDraftDto(draft.Summary, draft.Questions, draft.Variables, draft.Nodes, draft.Edges,
                    draft.EntryNodeId, draft.Problems, draft.Requests, draft.Usage), Core.Serialization.PepaJson.Options);
            }
            catch (Assistant.AssistantException ex)
            {
                return Results.Json(new ErrorDto(ex.Message), Core.Serialization.PepaJson.Options, statusCode: ex.Kind switch
                {
                    Assistant.AssistantErrorKind.Input => StatusCodes.Status400BadRequest,
                    Assistant.AssistantErrorKind.Key => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status502BadGateway,
                });
            }
        });

        api.MapGet("/templates", (Core.Nodes.NodeRegistry registry) => Samples.SampleScenarios.All().Select(s =>
        {
            var name = System.Text.RegularExpressions.Regex.Replace(s.Name, @"^Пример \d+\.\s*", "");
            var blocks = s.Nodes.Select(n => registry.FindDescriptor(n.Type))
                .Where(d => d is { IsStart: false, IsEnd: false })
                .Select(d => d!.Title).Distinct().ToList();
            return new TemplateDto(name, s.Description, blocks, s with { Name = name });
        }).ToList());

        api.MapPost("/scenarios", async (ScenarioDefinition definition, ScenarioService scenarios, EventHub events, CancellationToken ct) =>
        {
            var stored = await scenarios.CreateAsync(definition, ct);
            events.Publish("scenarios");
            return Results.Ok(ToDto(stored, scenarios));
        });

        api.MapPut("/scenarios/{id}", async (string id, SaveScenarioRequest request, ScenarioService scenarios, EventHub events,
            CancellationToken ct) =>
        {
            var stored = await scenarios.UpdateAsync(id, request.Definition, request.ExpectedVersion, ct);
            events.Publish("scenarios");
            events.Publish("scenario", id);
            return Results.Ok(ToDto(stored, scenarios));
        });

        api.MapDelete("/scenarios/{id}", async (string id, ScenarioService scenarios, EventHub events, CancellationToken ct) =>
        {
            if (!await scenarios.DeleteAsync(id, ct))
            {
                return NotFound("Сценарий не найден.");
            }

            events.Publish("scenarios");
            events.Publish("triggers");
            return Results.Ok(new MessageDto("Сценарий удалён вместе с его расписаниями. История его запусков сохранена."));
        });

        api.MapPost("/scenarios/{id}/duplicate", async (string id, ScenarioService scenarios, EventHub events, CancellationToken ct) =>
        {
            var copy = await scenarios.DuplicateAsync(id, ct);
            events.Publish("scenarios");
            return Results.Ok(ToDto(copy, scenarios));
        });

        api.MapPost("/scenarios/validate", (ScenarioDefinition definition, ScenarioService scenarios) =>
            scenarios.Validate(definition));

        api.MapPost("/scenarios/{id}/run", async (string id, RunRequest? request, ProcessEngine engine, CancellationToken ct) =>
            new RunResponse(await engine.StartProcessAsync(id, request?.Inputs, request?.StepMode == true, ct)));

        // ───────────── Процессы ─────────────
        api.MapGet("/processes", async (string? view, string? scenarioId, int? limit, IPepaStore store, ProcessMapper mapper,
            CancellationToken ct) =>
        {
            var statuses = view switch
            {
                "active" => ActiveStatuses,
                "finished" => FinishedStatuses,
                "attention" => [ProcessStatus.NeedsAttention],
                _ => null,
            };
            var processes = await store.ListProcessesAsync(new ProcessQuery
            {
                Statuses = statuses,
                ScenarioId = string.IsNullOrWhiteSpace(scenarioId) ? null : scenarioId,
                Limit = Math.Clamp(limit ?? 200, 1, 1000),
            }, ct);
            return await mapper.SummariesAsync(processes, ct);
        });

        api.MapGet("/processes/{id}", async (string id, IPepaStore store, ProcessMapper mapper, CancellationToken ct) =>
        {
            var process = await store.GetProcessAsync(id, ct);
            if (process is null)
            {
                return NotFound("Процесс не найден.");
            }

            var tokens = await store.GetTokensAsync(id, ct);
            var steps = await store.GetStepsAsync(id, ct);
            var journal = await store.GetJournalAsync(id, ct);
            var subscriptions = await store.GetSubscriptionsAsync(id, ct);
            var parent = process.ParentProcessId is { } parentId ? await store.GetProcessAsync(parentId, ct) : null;
            var children = await store.GetChildProcessesAsync(id, ct);
            var held = (await store.ListResourcesAsync(ct)).Where(r => r.HolderProcessId == id).Select(r => r.Key).ToList();
            return Results.Ok(new ProcessDetailsDto(
                mapper.Summary(process, tokens, subscriptions.ToDictionary(s => s.Id)),
                process.Variables,
                process.LastError,
                steps.Select(ProcessMapper.Step).ToList(),
                journal.Select(ProcessMapper.Journal).ToList(),
                Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(process.DefinitionJson),
                parent is null ? null : ProcessMapper.Ref(parent),
                await mapper.SummariesAsync(children, ct),
                subscriptions.Select(ProcessMapper.Wait).ToList(),
                held,
                // Где сейчас ветви процесса (у параллельного участка их несколько).
                tokens.Where(t => t.State != TokenState.Completed).Select(t => t.NodeId).Distinct().ToList()));
        });

        api.MapPost("/processes/{id}/pause", async (string id, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.PauseAsync(id, ct)).Message));

        api.MapPost("/processes/{id}/cancel", async (string id, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.CancelAsync(id, ct)).Message));

        api.MapPost("/processes/{id}/resume", async (string id, ResumeRequest request, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.ResumeAsync(id, request.Action, ct)).Message));

        // ───────────── События ─────────────
        api.MapGet("/events", async (string? view, int? limit, IPepaStore store, ProcessMapper mapper, CancellationToken ct) =>
        {
            var states = view switch
            {
                "open" => new[] { EventState.Received, EventState.Matched, EventState.Conflict },
                "conflict" => [EventState.Conflict],
                "unmatched" => [EventState.Unmatched],
                _ => null,
            };
            var records = await store.ListEventsAsync(new EventQuery { States = states, Limit = Math.Clamp(limit ?? 200, 1, 1000) }, ct);
            return await mapper.EventsAsync(records, ct);
        });

        api.MapPost("/events", async (PublishEventRequest request, ProcessEngine engine, CancellationToken ct) =>
        {
            var result = await engine.PublishEventAsync(new Core.EventInput
            {
                Source = request.Source?.Trim() ?? "",
                Type = request.Type?.Trim() ?? "",
                CorrelationKey = string.IsNullOrEmpty(request.CorrelationKey) ? null : request.CorrelationKey,
                ExternalId = string.IsNullOrWhiteSpace(request.ExternalId) ? null : request.ExternalId.Trim(),
                Payload = request.Payload?.DeepClone(),
            }, ct);
            return new PublishEventResponse(result.EventId, result.Duplicate);
        });

        api.MapPost("/events/{id:long}/resolve", async (long id, ResolveEventRequest request, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.ResolveEventAsync(id, request.SubscriptionId, ct)).Message));

        api.MapPost("/events/{id:long}/reroute", async (long id, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.RerouteEventAsync(id, ct)).Message));

        // ───────────── Ресурсы ─────────────
        api.MapGet("/resources", (ProcessMapper mapper, CancellationToken ct) => mapper.ResourcesAsync(ct));

        api.MapPost("/resources/release", async (ReleaseResourceRequest request, ProcessEngine engine, EventHub events,
            CancellationToken ct) =>
        {
            var result = await engine.ForceReleaseResourceAsync(request.Key, ct);
            events.Publish("resources");
            return new MessageDto(result.Message);
        });

        // ───────────── Триггеры ─────────────
        api.MapGet("/triggers", async (string? scenarioId, IPepaStore store, ProcessMapper mapper, CancellationToken ct) =>
            await mapper.TriggersAsync(await store.ListTriggersAsync(string.IsNullOrWhiteSpace(scenarioId) ? null : scenarioId, ct), ct));

        api.MapPost("/triggers", async (SaveTriggerRequest request, ProcessEngine engine, ProcessMapper mapper, CancellationToken ct) =>
            await SaveTriggerAsync(null, request, engine, mapper, ct));

        api.MapPut("/triggers/{id}", async (string id, SaveTriggerRequest request, ProcessEngine engine, ProcessMapper mapper,
            IPepaStore store, CancellationToken ct) =>
            await store.GetTriggerAsync(id, ct) is null
                ? NotFound("Запуск по расписанию или событию не найден.")
                : Results.Ok(await SaveTriggerAsync(id, request, engine, mapper, ct)));

        api.MapDelete("/triggers/{id}", async (string id, ProcessEngine engine, CancellationToken ct) =>
            await engine.DeleteTriggerAsync(id, ct)
                ? Results.Ok(new MessageDto("Удалено."))
                : NotFound("Запуск по расписанию или событию не найден."));

        api.MapPost("/triggers/{id}/run", async (string id, ProcessEngine engine, CancellationToken ct) =>
            new RunResponse(await engine.RunTriggerNowAsync(id, ct)));

        api.MapPost("/triggers/{id}/dismiss", async (string id, ProcessEngine engine, CancellationToken ct) =>
        {
            await engine.DismissMissedRunsAsync(id, ct);
            return new MessageDto("Пропущенные запуски отмечены как просмотренные.");
        });

        // ───────────── Поток изменений ─────────────
        api.MapGet("/stream", async (HttpContext http, EventHub hub) =>
        {
            var cancellation = http.RequestAborted;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.WriteAsync("retry: 2000\n\n", cancellation);
            await http.Response.Body.FlushAsync(cancellation);

            var (subscription, reader) = hub.Subscribe();
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    tick.CancelAfter(TimeSpan.FromSeconds(15));
                    string line;
                    try
                    {
                        line = $"data: {await reader.ReadAsync(tick.Token)}\n\n";
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        line = ": ping\n\n";
                    }

                    await http.Response.WriteAsync(line, cancellation);
                    await http.Response.Body.FlushAsync(cancellation);
                }
            }
            catch (OperationCanceledException)
            {
                // Окно закрыто.
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                // Приложение останавливается.
            }
            finally
            {
                hub.Unsubscribe(subscription);
            }
        });
    }

    private static async Task<TriggerDto> SaveTriggerAsync(string? id, SaveTriggerRequest request, ProcessEngine engine,
        ProcessMapper mapper, CancellationToken ct)
    {
        var saved = await engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = id ?? "",
            ScenarioId = request.ScenarioId,
            Kind = request.Kind,
            Enabled = request.Enabled,
            Name = request.Name ?? "",
            Config = (System.Text.Json.Nodes.JsonObject)request.Config.DeepClone(),
            Inputs = request.Inputs?.DeepClone() as System.Text.Json.Nodes.JsonObject,
            Overlap = request.Overlap ?? "skip",
            CreatedAt = default,
            UpdatedAt = default,
        }, ct);
        return (await mapper.TriggersAsync([saved], ct))[0];
    }

    private static RecorderStateDto RecorderDto(Adapters.Browser.RecorderState state) =>
        new(state.Recording, state.Browser?.ToString().ToLowerInvariant(), state.ProfileId, state.StartUrl, state.Actions);

    private static TelegramStatusDto TelegramDto(Adapters.Telegram.TelegramService telegram)
    {
        var status = telegram.Status;
        var saved = telegram.SavedLogin;
        return new TelegramStatusDto(JsonNamingPolicy.CamelCase.ConvertName(status.State.ToString()), status.Account, status.Message,
            saved?.ApiId, saved?.Phone);
    }

    private static AssistantStatusDto AssistantDto(string? key, Assistant.ScenarioAssistant assistant) =>
        new(key is not null, key is null ? null : AssistantKeyStore.Hint(key), assistant.Model);

    private static SettingsDto ToDto(SettingsService settings)
    {
        var current = settings.Current;
        return new SettingsDto(current.WorkspaceRoot, current.MaxParallelProcesses, current.StepBudget,
            current.ActionTimeoutSeconds, settings.RestartRequired, current.AdsPowerUrl, null, current.AdsPowerApiKey is not null);
    }

    private static ScenarioDto ToDto(StoredScenario stored, ScenarioService scenarios)
    {
        var definition = ScenarioService.Parse(stored);
        return new ScenarioDto(stored.Id, stored.Name, stored.Description, stored.Version, stored.CreatedAt,
            stored.UpdatedAt, definition, scenarios.Validate(definition));
    }

    private static IResult NotFound(string message) => Results.NotFound(new ErrorDto(message));

    /// <summary>Ошибки предметной области — в понятные ответы API.</summary>
    public static async Task HandleErrorsAsync(HttpContext http, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception ex) when (!http.Response.HasStarted && Map(ex) is { } mapped)
        {
            http.Response.StatusCode = mapped.Status;
            await http.Response.WriteAsJsonAsync(mapped.Body, Core.Serialization.PepaJson.Options);
        }
    }

    private static (int Status, ErrorDto Body)? Map(Exception ex) => ex switch
    {
        ScenarioInvalidException invalid => (StatusCodes.Status422UnprocessableEntity, new ErrorDto(invalid.Message, invalid.Report)),
        EngineOperationException operation => (StatusCodes.Status409Conflict, new ErrorDto(operation.Message)),
        ConcurrencyException concurrency => (StatusCodes.Status409Conflict, new ErrorDto(concurrency.Message)),
        SettingsValidationException settings => (StatusCodes.Status400BadRequest, new ErrorDto(settings.Message)),
        BadHttpRequestException bad => (StatusCodes.Status400BadRequest, new ErrorDto($"Некорректный запрос: {bad.Message}")),
        JsonException json => (StatusCodes.Status400BadRequest, new ErrorDto($"Некорректные данные: {json.Message}")),
        _ => null,
    };
}
