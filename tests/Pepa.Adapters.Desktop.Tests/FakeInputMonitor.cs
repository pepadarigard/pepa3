namespace Pepa.Adapters.Desktop.Tests;

/// <summary>Имитация перехвата мыши и клавиатуры: тест сам «нажимает» и «печатает».</summary>
internal sealed class FakeInputMonitor : IDesktopInputMonitor
{
    private Action<DesktopInput>? _sink;

    public string? UnavailableReason { get; set; }

    /// <summary>Что передать при остановке (незаконченный ввод текста).</summary>
    public List<DesktopInput> OnStop { get; } = [];

    public bool Watching => _sink is not null;

    /// <summary>Ошибка при запуске, не похожая на ошибку Windows (сбой самого перехватчика).</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Остановка не удаётся (перехватчик завис).</summary>
    public bool StopFails { get; set; }

    /// <summary>Все переданные получатели — чтобы проверить, что старый перехват не пишет в новую запись.</summary>
    public List<Action<DesktopInput>> Sinks { get; } = [];

    public IDesktopInputWatch Start(Action<DesktopInput> sink)
    {
        if (UnavailableReason is { } reason)
        {
            throw new DesktopException(reason);
        }

        if (StartFailure is { } failure)
        {
            throw failure;
        }

        _sink = sink;
        Sinks.Add(sink);
        return new Watch(this);
    }

    public void Send(DesktopInput input) => (_sink ?? throw new InvalidOperationException("Перехват не запущен."))(input);

    private sealed class Watch(FakeInputMonitor monitor) : IDesktopInputWatch
    {
        public Task StopAsync()
        {
            if (monitor.StopFails)
            {
                return Task.FromException(new TimeoutException("перехват не ответил"));
            }

            foreach (var input in monitor.OnStop)
            {
                monitor.Send(input);
            }

            monitor.OnStop.Clear();
            monitor._sink = null;
            return Task.CompletedTask;
        }
    }
}
