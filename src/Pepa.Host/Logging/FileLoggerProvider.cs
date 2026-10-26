using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Pepa.Host.Logging;

/// <summary>
/// Простой журнал платформы в файлы logs/pepa-ГГГГММДД.log (для диагностики). История процессов
/// хранится отдельно — в базе.
/// </summary>
internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"pepa-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Журнал диагностики не должен мешать работе.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            owner.Write(builder.ToString());
        }
    }
}
