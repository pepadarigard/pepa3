namespace Pepa.Host;

public sealed class InstanceAlreadyRunningException(string message) : Exception(message);

/// <summary>
/// Монопольная блокировка папки данных: два экземпляра над одной базой исполнили бы одни и те же шаги дважды.
/// Блокировка снимается операционной системой и при аварийном завершении процесса.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "instance.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write($"{Environment.ProcessId} {DateTimeOffset.Now:O}");
            }

            stream.Flush();
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            throw new InstanceAlreadyRunningException(
                $"Pepa уже запущена и работает с папкой данных «{dataDirectory}». Закройте другой экземпляр.");
        }
    }

    public void Dispose() => _stream.Dispose();
}
