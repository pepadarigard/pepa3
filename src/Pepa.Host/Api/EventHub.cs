using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Pepa.Host.Api;

/// <summary>
/// Рассылка изменений в открытые окна интерфейса (Server-Sent Events). Частые изменения одного процесса
/// склеиваются: интерфейс получает не больше одного уведомления на объект за такт.
/// </summary>
public sealed class EventHub : IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly ConcurrentDictionary<(string Type, string? Id), byte> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _disposed;

    public EventHub() => _loop = Task.Run(FlushLoopAsync);

    public void Publish(string type, string? id = null) => _pending.TryAdd((type, id), 0);

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (_pending.IsEmpty)
                {
                    continue;
                }

                var keys = _pending.Keys.ToList();
                foreach (var key in keys)
                {
                    _pending.TryRemove(key, out _);
                }

                foreach (var (type, id) in keys)
                {
                    var message = JsonSerializer.Serialize(new { type, id });
                    foreach (var subscriber in _subscribers.Values)
                    {
                        subscriber.Writer.TryWrite(message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync();
        await _loop;
        foreach (var id in _subscribers.Keys)
        {
            Unsubscribe(id);
        }

        _stop.Dispose();
    }
}
