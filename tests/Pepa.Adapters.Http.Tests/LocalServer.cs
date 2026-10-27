using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pepa.Adapters.Http.Tests;

/// <summary>Простейший сервер HTTP на петлевом адресе: настоящий клиент Pepa, настоящие сокеты, заданный ответ.</summary>
public sealed class LocalServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _stop = new();

    private LocalServer(Func<string, string> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public List<string> Requests { get; } = [];

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public static Task<LocalServer> StartAsync(Func<string, string> respond)
    {
        var server = new LocalServer(respond);
        server._listener.Start();
        _ = server.LoopAsync();
        return Task.FromResult(server);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[16 * 1024];
                    var text = new StringBuilder();
                    while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            return;
                        }

                        text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    lock (Requests)
                    {
                        Requests.Add(text.ToString());
                    }

                    var response = Encoding.UTF8.GetBytes(_respond(text.ToString()).Replace("Content-Length:", "Connection: close\r\nContent-Length:", StringComparison.Ordinal));
                    await stream.WriteAsync(response);
                }
            });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
