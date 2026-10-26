using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Pepa.Host.Security;

/// <summary>
/// Доступ к API только с этого компьютера и только из окна Pepa:
/// заголовок Host — 127.0.0.1/localhost с нашим портом (защита от DNS rebinding),
/// для /api — секретный токен в заголовке X-Pepa-Token (сторонний сайт в браузере его не знает).
/// </summary>
internal sealed class LocalAccessMiddleware(RequestDelegate next, HostContext context)
{
    public const string TokenHeader = "X-Pepa-Token";

    public async Task InvokeAsync(HttpContext http)
    {
        var host = http.Request.Host;
        var hostAllowed = host.Host is "127.0.0.1" or "localhost" &&
                          (host.Port is null || host.Port == context.Port || context.Port == 0);
        if (!hostAllowed)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Invalid host");
            return;
        }

        if (http.Request.Path.StartsWithSegments("/api"))
        {
            var provided = http.Request.Headers[TokenHeader].ToString();
            if (!TokensEqual(provided, context.Token))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await http.Response.WriteAsJsonAsync(new { error = "Нет доступа: неверный токен. Откройте Pepa заново." });
                return;
            }
        }

        // Интерфейс не встраивается в чужие страницы.
        http.Response.Headers["X-Frame-Options"] = "DENY";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next(http);
    }

    private static bool TokensEqual(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>Параметры запущенного экземпляра, нужные обработчикам запросов.</summary>
public sealed class HostContext
{
    public required string Token { get; init; }
    public required string DataDirectory { get; init; }
    public required bool IsDesktop { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public int Port { get; set; }
    public Engine.RecoveryReport Recovery { get; set; } = new(0, 0, 0);

    public static string Version =>
        typeof(HostContext).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
