using System.Collections.Concurrent;
using Pepa.Host.Power;

namespace Pepa.Host.Tests;

/// <summary>Таймер пробуждения для проверок: ничего не заводит в настоящей Windows, только запоминает.</summary>
public sealed class FakeWakePlatform : IWakePlatform
{
    private readonly AutoResetEvent _fired = new(false);

    public ConcurrentQueue<string> Calls { get; } = new();

    public bool Supported => true;

    public WaitHandle Fired => _fired;

    public bool Arm(DateTimeOffset at)
    {
        Calls.Enqueue($"arm {at:O}");
        return true;
    }

    public void Cancel() => Calls.Enqueue("cancel");

    public void HoldAwake(bool hold) => Calls.Enqueue(hold ? "hold" : "release");

    /// <summary>Наступило заведённое время.</summary>
    public void Fire() => _fired.Set();

    public void Dispose() => _fired.Dispose();
}
