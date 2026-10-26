namespace Pepa.Adapters.Desktop.Tests;

/// <summary>Имитация перехвата мыши и клавиатуры: тест сам «нажимает» и «печатает».</summary>
internal sealed class FakeInputMonitor : IDesktopInputMonitor
{
    private Action<DesktopInput>? _sink;

    public string? UnavailableReason { get; set; }

    /// <summary>Что передать при остановке (незаконченный ввод текста).</summary>
    public List<DesktopInput> OnStop { get; } = [];

    public bool Watching => _sink is not null;

    public IDesktopInputWatch Start(Action<DesktopInput> sink)
    {
        if (UnavailableReason is { } reason)
        {
            throw new DesktopException(reason);
        }

        _sink = sink;
        return new Watch(this);
    }

    public void Send(DesktopInput input) => (_sink ?? throw new InvalidOperationException("Перехват не запущен."))(input);

    private sealed class Watch(FakeInputMonitor monitor) : IDesktopInputWatch
    {
        public Task StopAsync()
        {
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
