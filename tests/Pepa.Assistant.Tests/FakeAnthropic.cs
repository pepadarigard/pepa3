using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Pepa.Assistant.Tests;

/// <summary>
/// Имитация Claude API на локальном порту: принимает запросы настоящего SDK, запоминает их и отвечает заготовками
/// по очереди (сообщение с текстом, отказ, ошибка с кодом).
/// </summary>
internal sealed class FakeAnthropic : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<(int Status, string Body)> _responses = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeAnthropic()
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Url { get; }

    /// <summary>Полученные запросы: путь, заголовки, тело.</summary>
    public ConcurrentQueue<(string Path, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)> Requests { get; } = new();

    /// <summary>Ответ модели — черновик (JSON строкой в текстовом блоке, как при структурированном выводе).</summary>
    public void ReplyDraft(JsonObject draft, string stopReason = "end_turn") =>
        _responses.Enqueue((200, new JsonObject
        {
            ["id"] = $"msg_{_responses.Count + 1}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-opus-5-5",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = draft.ToJsonString() }),
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 1200, ["output_tokens"] = 800, ["cache_read_input_tokens"] = _responses.IsEmpty ? 0 : 9000,
                ["cache_creation_input_tokens"] = 0,
            },
        }.ToJsonString()));

    public void ReplyRefusal() =>
        _responses.Enqueue((200, new JsonObject
        {
            ["id"] = "msg_refusal",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-opus-5-5",
            ["content"] = new JsonArray(),
            ["stop_reason"] = "refusal",
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 0 },
        }.ToJsonString()));

    public void ReplyError(int status, string type, string message) =>
        _responses.Enqueue((status, new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
        }.ToJsonString()));

    public void ReplyModel() =>
        _responses.Enqueue((200, new JsonObject
        {
            ["type"] = "model", ["id"] = "claude-opus-5-5", ["display_name"] = "Claude Opus 5.5", ["created_at"] = "2026-09-01T00:00:00Z",
        }.ToJsonString()));

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.AllKeys.Where(k => k is not null)
                .ToDictionary(k => k!.ToLowerInvariant(), k => context.Request.Headers[k] ?? "");
            Requests.Enqueue((context.Request.Url!.AbsolutePath, headers, body.Length > 0 ? JsonNode.Parse(body) : null));

            var (status, reply) = _responses.TryDequeue(out var next)
                ? next
                : (500, """{"type":"error","error":{"type":"api_error","message":"no canned response"}}""");
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(reply);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}
