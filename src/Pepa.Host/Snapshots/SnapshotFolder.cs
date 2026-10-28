using System.Text.RegularExpressions;
using Pepa.Core.Nodes;

namespace Pepa.Host.Snapshots;

/// <summary>
/// Снимки экрана при ошибках шагов — в папке данных Pepa (не в рабочей): <c>snapshots/&lt;процесс&gt;/001.jpg</c>. У процесса не
/// больше <see cref="MaxPerProcess"/> снимков — цикл, в котором шаг ошибается на каждом круге, не заполнит диск; снимки
/// удаляются вместе с историей процесса (D-072).
/// </summary>
public sealed partial class SnapshotFolder(string root) : ISnapshotStore
{
    public const int MaxPerProcess = 20;
    public const int MaxBytes = 10 * 1024 * 1024;

    public string Root { get; } = Path.GetFullPath(root);

    public async ValueTask<string?> SaveAsync(string processId, byte[] image, string extension, CancellationToken cancellationToken)
    {
        if (!ProcessIdPattern().IsMatch(processId) || extension is not (".jpg" or ".png") || image.Length is 0 or > MaxBytes)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(Root, processId);
            Directory.CreateDirectory(directory);
            // Параллельные ветви процесса могут сохранять одновременно: имя занимается созданием файла, а не подсчётом.
            for (var number = Directory.GetFiles(directory).Length + 1; number <= MaxPerProcess; number++)
            {
                var name = $"{number:000}{extension}";
                var path = Path.Combine(directory, name);
                if (Directory.GetFiles(directory, $"{number:000}.*").Length > 0)
                {
                    continue;
                }

                FileStream file;
                try
                {
                    file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(path))
                {
                    continue;
                }

                try
                {
                    await using (file)
                    {
                        await file.WriteAsync(image, cancellationToken);
                    }

                    return name;
                }
                catch
                {
                    TryDelete(path);
                    throw;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Снимок — подсказка: не сохранился (диск полон, нет доступа) — шаг от этого не меняется.
            return null;
        }
    }

    /// <summary>Файл снимка процесса или null (имя не снимка, файла нет).</summary>
    public string? Find(string processId, string name)
    {
        if (!ProcessIdPattern().IsMatch(processId) || !NamePattern().IsMatch(name))
        {
            return null;
        }

        var path = Path.Combine(Root, processId, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Удалить снимки процессов, которых больше нет в истории. Возвращает число удалённых папок.</summary>
    public async Task<int> DeleteOrphansAsync(Func<string, Task<bool>> processExists, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var directory in Directory.GetDirectories(Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!ProcessIdPattern().IsMatch(id) || await processExists(id))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл открыт (например, в просмотрщике) — удалится в следующий раз.
            }
        }

        return deleted;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ProcessIdPattern();

    [GeneratedRegex(@"^\d{3}\.(jpg|png)$")]
    private static partial Regex NamePattern();
}
