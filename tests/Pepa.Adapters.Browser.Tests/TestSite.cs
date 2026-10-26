using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>Маленький сайт для тестов на 127.0.0.1.</summary>
internal sealed class TestSite : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private int _submits;

    public TestSite()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    /// <summary>Сколько раз форма была отправлена (настоящий «внешний эффект» нажатия).</summary>
    public int Submits => Volatile.Read(ref _submits);

    public static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            var html = path switch
            {
                "/form" => """
                           <!doctype html><html><head><title>Форма</title></head><body>
                           <h1>Вход</h1>
                           <input id="user" placeholder="Имя">
                           <button id="go" onclick="fetch('/submit?u=' + encodeURIComponent(document.getElementById('user').value)).then(r => r.text()).then(t => { const d = document.createElement('div'); d.id = 'done'; d.textContent = t; document.body.appendChild(d); })">Отправить</button>
                           <ul><li class="item">Один</li><li class="item">Два</li><li class="item">Три</li></ul>
                           <a id="next" href="/second">Дальше</a>
                           </body></html>
                           """,
                "/second" => "<!doctype html><html><head><title>Вторая</title></head><body><p id='msg'>Вторая страница</p></body></html>",
                "/late" => """
                           <!doctype html><html><head><title>Позже</title></head><body>
                           <script>setTimeout(() => { const b = document.createElement('button'); b.id = 'late'; b.textContent = 'Появилась'; document.body.appendChild(b); }, 1500);</script>
                           </body></html>
                           """,
                "/controls" => """
                           <!doctype html><html><head><title>Элементы</title></head><body>
                           <select id="city"><option value="msk">Москва</option><option value="spb">Санкт-Петербург</option></select>
                           <div id="fake-select">Не список</div>
                           <label><input type="checkbox" id="agree"> Согласен</label>
                           <input type="file" id="file" style="display:none">
                           <a id="popup" href="/second?from=popup" target="_blank">Открыть во вкладке</a>
                           <button id="later" onclick="setTimeout(() => location.href = '/second?step=done', 700)">Перейти позже</button>
                           </body></html>
                           """,
                "/submit" => $"Принято: {context.Request.QueryString["u"]}",
                _ => null,
            };

            if (path == "/submit")
            {
                Interlocked.Increment(ref _submits);
            }

            context.Response.StatusCode = html is null ? 404 : 200;
            context.Response.ContentType = path == "/submit" ? "text/plain; charset=utf-8" : "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html ?? "not found");
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}
