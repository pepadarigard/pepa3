using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Pepa.Core;

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
