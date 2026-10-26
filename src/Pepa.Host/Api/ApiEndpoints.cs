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
            await settings.SaveAsync(new PlatformSettings
            {
                WorkspaceRoot = request.WorkspaceRoot,
                MaxParallelProcesses = request.MaxParallelProcesses,
                StepBudget = request.StepBudget,
                ActionTimeoutSeconds = request.ActionTimeoutSeconds,
            });
            return ToDto(settings);
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
                await mapper.SummariesAsync(recent, ct));
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
            return Results.Ok(new MessageDto("Сценарий удалён. История его запусков сохранена."));
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
            new RunResponse(await engine.StartProcessAsync(id, request?.Inputs, ct)));

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
            return Results.Ok(new ProcessDetailsDto(
                mapper.Summary(process, tokens),
                process.Variables,
                process.LastError,
                steps.Select(ProcessMapper.Step).ToList(),
                journal.Select(ProcessMapper.Journal).ToList(),
                Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(process.DefinitionJson)));
        });

        api.MapPost("/processes/{id}/pause", async (string id, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.PauseAsync(id, ct)).Message));

        api.MapPost("/processes/{id}/cancel", async (string id, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.CancelAsync(id, ct)).Message));

        api.MapPost("/processes/{id}/resume", async (string id, ResumeRequest request, ProcessEngine engine, CancellationToken ct) =>
            new MessageDto((await engine.ResumeAsync(id, request.Action, ct)).Message));

        // ───────────── Поток изменений ─────────────
        api.MapGet("/events", async (HttpContext http, EventHub hub) =>
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

    private static SettingsDto ToDto(SettingsService settings)
    {
        var current = settings.Current;
        return new SettingsDto(current.WorkspaceRoot, current.MaxParallelProcesses, current.StepBudget,
            current.ActionTimeoutSeconds, settings.RestartRequired);
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
