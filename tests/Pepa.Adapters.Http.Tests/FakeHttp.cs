using System.Net;
using System.Text;

namespace Pepa.Adapters.Http.Tests;

/// <summary>Имитация сервера: отвечает заданной функцией и запоминает каждый полученный запрос.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> _respond;

    public FakeHttp(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : this((r, b, _) => Task.FromResult(respond(r, b)))
    {
    }

    private FakeHttp(Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    /// <summary>Сервер, который отвечает не сразу (запрос можно прервать).</summary>
    public static FakeHttp Slow(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new((r, _, ct) => respond(r, ct));

    public List<Received> Requests { get; } = [];

    public sealed record Received(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body, string? ContentType);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Requests)
        {
            Requests.Add(new Received(request.Method, request.RequestUri!, headers, body, request.Content?.Headers.ContentType?.ToString()));
        }

        var response = await _respond(request, body, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}
