using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Pepa.Core;
using TL;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace Pepa.Adapters.Telegram.Tests;

/// <summary>
/// Подключение и вход без настоящего Telegram: «сервер» — локальный порт, который принимает соединение и молчит
/// (как недоступный Telegram), или закрытый порт.
/// </summary>
public sealed class TelegramServiceTests : IAsyncLifetime
{
    private const string ApiHash = "0123456789abcdef0123456789abcdef";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pepa-telegram-tests", Guid.NewGuid().ToString("N"));
    private readonly TcpListener _silent = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _accepted = [];

    public ValueTask InitializeAsync()
    {
        _silent.Start();
        _ = AcceptForeverAsync();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _silent.Stop();
        foreach (var client in _accepted)
        {
            client.Dispose();
        }

        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private async Task AcceptForeverAsync()
    {
        try
        {
            while (true)
            {
                _accepted.Add(await _silent.AcceptTcpClientAsync());
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private string SilentAddress => $"127.0.0.1:{((IPEndPoint)_silent.LocalEndpoint).Port}";

    private TelegramService Create(string address, TimeSpan? timeout = null) =>
        new(_data, _ => Task.CompletedTask) { ServerAddress = address, ConnectTimeout = timeout ?? TimeSpan.FromSeconds(30) };

    private sealed class FullDisk : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("На диске нет места."));
    }

    [Fact]
    public async Task Disk_error_while_downloading_is_told_apart_from_a_broken_connection()
    {
        var stream = new DiskErrorStream(new FullDisk());
        await Assert.ThrowsAsync<IOException>(async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken));
        Assert.Equal("На диске нет места.", stream.Failure?.Message);
        Assert.Null(new DiskErrorStream(new MemoryStream()).Failure);
    }

    [Fact]
    public void Api_hash_and_phone_are_checked_before_connecting()
    {
        Assert.True(TelegramService.IsApiHash(ApiHash));
        Assert.False(TelegramService.IsApiHash("abc"));
        Assert.False(TelegramService.IsApiHash(new string('z', 32)));
        Assert.Equal("+79001234567", TelegramService.NormalizePhone("+7 (900) 123-45-67"));
    }

    [Fact]
    public async Task Silent_server_gives_clear_timeout_and_login_form_again()
    {
        await using var telegram = Create(SilentAddress, TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<TelegramLoginException>(() =>
            telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567")));
        Assert.Contains("не ответил", error.Message);
        Assert.Equal(TelegramState.LoginRequired, telegram.Status.State);
        Assert.Equal((12345, "+79001234567"), telegram.SavedLogin);
        Assert.False(File.Exists(Path.Combine(_data, "telegram", "session.dat")));

        // Новый экземпляр (перезапуск Pepa): сессии нет — подключение само не начинается.
        await using var restarted = Create(SilentAddress);
        await restarted.StartAsync();
        Assert.Equal(TelegramState.LoginRequired, restarted.Status.State);
    }

    [Fact]
    public async Task Closing_Pepa_interrupts_pending_login_quickly()
    {
        var telegram = Create(SilentAddress, TimeSpan.FromMinutes(5));
        var login = telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567"));
        await WaitUntilAsync(() => telegram.Status.State == TelegramState.Connecting);

        var watch = Stopwatch.StartNew();
        await telegram.DisposeAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"закрытие заняло {watch.Elapsed}");
        var error = await Assert.ThrowsAsync<TelegramLoginException>(() => login);
        Assert.Contains("закрывается", error.Message);
    }

    [Fact]
    public async Task Refused_connection_is_reported_and_blocks_say_not_connected()
    {
        await using var telegram = Create("127.0.0.1:1");
        var error = await Assert.ThrowsAsync<TelegramLoginException>(() =>
            telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567")));
        Assert.Contains("Не удалось связаться", error.Message);

        var unavailable = await Assert.ThrowsAsync<TelegramUnavailableException>(() =>
            telegram.ResolveAsync("@ivan", TestContext.Current.CancellationToken));
        Assert.Contains("Интеграции", unavailable.Message);

        await Assert.ThrowsAsync<TelegramLoginException>(() => telegram.ContinueLoginAsync("12345"));
        await telegram.LogoutAsync();
        Assert.Equal(TelegramState.NotConfigured, telegram.Status.State);
        Assert.Null(telegram.SavedLogin);
    }

    // ───────────── Входящие сообщения через настоящий менеджер обновлений библиотеки ─────────────

    private string StatePath => Path.Combine(_data, "telegram", "updates.json");

    private static readonly TelegramCredentials Account = new(12345, ApiHash, "+79001234567");

    /// <summary>Подать обновление так, как его подаёт клиент библиотеки (обработчик менеджера — закрытый метод).</summary>
    private static Task InjectAsync(WTelegram.UpdateManager manager, int pts, int messageId, string text)
    {
        var updates = new Updates
        {
            updates =
            [
                new UpdateNewMessage
                {
                    pts = pts, pts_count = 1,
                    message = new Message { id = messageId, peer_id = new PeerUser { user_id = 42 }, message = text, date = DateTime.UtcNow },
                },
            ],
            users = [], chats = [], date = DateTime.UtcNow,
        };
        var handler = typeof(WTelegram.UpdateManager).GetMethod("OnUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Task.Run(() => (Task)handler.Invoke(manager, [updates, false])!);
    }

    private int SavedPts() => JsonNode.Parse(File.ReadAllText(StatePath))!["0"]!["pts"]!.GetValue<int>();

    private void WriteState(int pts) =>
        File.WriteAllText(StatePath, new JsonObject
        {
            ["-2"] = new JsonObject { ["pts"] = 0, ["access_hash"] = 0 },
            ["-1"] = new JsonObject { ["pts"] = 0, ["access_hash"] = 0 },
            ["0"] = new JsonObject { ["pts"] = pts, ["access_hash"] = 0 },
        }.ToJsonString());

    [Fact]
    public async Task Incoming_messages_are_accepted_in_order_and_state_is_saved_outside_the_handler()
    {
        var events = new ConcurrentQueue<EventInput>();
        var service = new TelegramService(_data, e => { events.Enqueue(e); return Task.Delay(20); }) { ServerAddress = "127.0.0.1:1" };
        var manager = await service.ConnectForTestsAsync(Account);

        // Раньше обработчик сохранял состояние сам и навсегда блокировал библиотеку на первом же сообщении.
        await InjectAsync(manager, 1, 100, "первое").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await InjectAsync(manager, 2, 101, "второе").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["первое", "второе"], events.Select(e => e.Payload!["text"]!.GetValue<string>()));
        Assert.Equal("0:user:42:100", events.First().ExternalId);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, SavedPts());
    }

    [Fact]
    public async Task Skipped_updates_reported_by_the_library_are_shown_and_state_is_not_advanced()
    {
        Directory.CreateDirectory(Path.Combine(_data, "telegram"));
        WriteState(5);
        var service = new TelegramService(_data, _ => Task.CompletedTask) { ServerAddress = "127.0.0.1:1" };
        var manager = await service.ConnectForTestsAsync(Account);

        // Так библиотека сообщает, что запрос пропущенных обновлений упал, — и всё равно сдвигает позицию.
        manager.Log(4, "GetDifference(0, 5->9) raised WTelegram.RpcException: FLOOD_WAIT_X");
        Assert.Contains("могли не дойти", service.Status.Message);
        await InjectAsync(manager, 6, 300, "после пропуска").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Позиция не сохранена: после перезапуска библиотека запросит пропущенное снова.
        Assert.Equal(5, SavedPts());
    }

    [Fact]
    public async Task Event_time_is_corrected_for_a_computer_clock_that_runs_fast()
    {
        var events = new ConcurrentQueue<EventInput>();
        var service = new TelegramService(_data, e => { events.Enqueue(e); return Task.CompletedTask; })
        {
            ServerAddress = "127.0.0.1:1",
            // Часы компьютера спешат на 10 минут.
            ServerClockForTests = () => Task.FromResult(DateTime.UtcNow.AddMinutes(-10)),
        };
        var manager = await service.ConnectForTestsAsync(Account);
        var before = DateTimeOffset.UtcNow;
        await InjectAsync(manager, 1, 400, "время").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var occurred = events.Single().OccurredAt!.Value;
        // Время сообщения (по Telegram — «сейчас») переведено на часы компьютера: +10 минут.
        Assert.InRange(occurred - before, TimeSpan.FromMinutes(9.9), TimeSpan.FromMinutes(10.1));
        Assert.Contains("спешат на 10 мин", service.Status.Message);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clock_correction_follows_when_the_computer_clock_is_set_right()
    {
        var events = new ConcurrentQueue<EventInput>();
        var shift = TimeSpan.FromMinutes(10);
        var service = new TelegramService(_data, e => { events.Enqueue(e); return Task.CompletedTask; })
        {
            ServerAddress = "127.0.0.1:1",
            // Часы компьютера спешат на 10 минут, потом пользователь их поправляет.
            WallClockForTests = () => DateTimeOffset.UtcNow + shift,
            ServerClockForTests = () => Task.FromResult(DateTime.UtcNow),
        };
        var manager = await service.ConnectForTestsAsync(Account);
        await InjectAsync(manager, 1, 400, "до").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        shift = TimeSpan.Zero;
        await InjectAsync(manager, 2, 401, "после").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var times = events.Select(e => e.OccurredAt!.Value - DateTimeOffset.UtcNow).ToArray();
        Assert.InRange(times[0], TimeSpan.FromMinutes(9.8), TimeSpan.FromMinutes(10.1));
        Assert.InRange(times[1], TimeSpan.FromSeconds(-15), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("спешат", service.Status.Message ?? "");
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Message_that_could_not_be_accepted_keeps_the_old_state_so_it_comes_again()
    {
        Directory.CreateDirectory(Path.Combine(_data, "telegram"));
        WriteState(5);
        var service = new TelegramService(_data, _ => throw new InvalidOperationException("база занята")) { ServerAddress = "127.0.0.1:1", PublishAttempts = 2 };
        var manager = await service.ConnectForTestsAsync(Account);

        await InjectAsync(manager, 6, 200, "важное").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("придут снова", service.Status.Message);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Состояние не продвинулось: после перезапуска Telegram пришлёт сообщение снова (повтор отсеется по ключу).
        Assert.Equal(5, SavedPts());
    }

    [Fact]
    public async Task Temporary_failure_is_retried_and_then_state_is_saved()
    {
        var calls = 0;
        var events = new ConcurrentQueue<EventInput>();
        var service = new TelegramService(_data, e =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("диск занят");
            }

            events.Enqueue(e);
            return Task.CompletedTask;
        }) { ServerAddress = "127.0.0.1:1" };
        var manager = await service.ConnectForTestsAsync(Account);
        await InjectAsync(manager, 1, 300, "с повтором").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(events);
        Assert.Null(service.Status.Message);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, SavedPts());
    }

    [Fact]
    public async Task Closing_while_a_message_is_being_retried_does_not_wait_and_does_not_save_state()
    {
        Directory.CreateDirectory(Path.Combine(_data, "telegram"));
        WriteState(5);
        var attempts = 0;
        var service = new TelegramService(_data, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("движок остановлен");
        }) { ServerAddress = "127.0.0.1:1", PublishAttempts = 100 };
        var manager = await service.ConnectForTestsAsync(Account);
        var handler = InjectAsync(manager, 6, 400, "в процессе закрытия");
        await WaitUntilAsync(() => Volatile.Read(ref attempts) > 0);

        var watch = Stopwatch.StartNew();
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"закрытие заняло {watch.Elapsed}");
        await handler.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(5, SavedPts());
    }

    [Fact]
    public async Task Damaged_state_file_is_reported_and_does_not_block_the_session()
    {
        Directory.CreateDirectory(Path.Combine(_data, "telegram"));
        await File.WriteAllTextAsync(StatePath, "{\"0\":{\"pts", TestContext.Current.CancellationToken);
        var events = new ConcurrentQueue<EventInput>();
        var service = new TelegramService(_data, e => { events.Enqueue(e); return Task.CompletedTask; }) { ServerAddress = "127.0.0.1:1" };
        var manager = await service.ConnectForTestsAsync(Account);
        Assert.Contains("повреждён", service.Status.Message);
        await InjectAsync(manager, 1, 500, "после сбоя").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(events);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, SavedPts());

        // Сессия не осталась занятой: следующее подключение открывается.
        await using var again = new TelegramService(_data, _ => Task.CompletedTask) { ServerAddress = "127.0.0.1:1" };
        await again.ConnectForTestsAsync(Account);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "условие не выполнилось за 10 секунд");
            await Task.Delay(20);
        }
    }
}
