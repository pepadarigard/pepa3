using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Pepa.Adapters.FileSystem;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables;

/// <summary>Запись не состоялась, но может получиться позже (файл открыт в Excel, занят, изменился во время записи).</summary>
internal sealed class TableBusyException(string message) : Exception(message);

/// <summary>Прочитанный файл таблицы: лист, исходные байты и отметка, по которой видно, что файл с тех пор не менялся.</summary>
internal sealed record TableSnapshot(TableSheet Sheet, byte[] Bytes, (long Length, DateTime Written) Stamp, CsvDocument? Csv);

/// <summary>
/// Файл таблицы в рабочей папке: .xlsx/.xlsm (Excel) или .csv/.tsv. Запись — целиком во временный файл рядом и
/// заменой одним переименованием: таблица всегда либо прежняя, либо с изменением, но не наполовину записанная.
/// </summary>
internal sealed class TableFile
{
    /// <summary>Больше файл не читается: лист разбирается в памяти целиком.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private TableFile(string fullPath, string relative, bool csv)
    {
        FullPath = fullPath;
        Relative = relative;
        IsCsv = csv;
    }

    public string FullPath { get; }
    public string Relative { get; }
    public bool IsCsv { get; }
    public bool Exists => File.Exists(FullPath);

    public static TableFile Open(IWorkspace workspace, string? path)
    {
        var full = workspace.Resolve(path);
        var relative = workspace.ToRelative(full);
        var extension = Path.GetExtension(full).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" or ".xlsm" => new TableFile(full, relative, csv: false),
            ".csv" or ".tsv" => new TableFile(full, relative, csv: true),
            ".xls" => throw new NodeInputException(
                $"«{relative}» — старый формат Excel (.xls). Откройте файл в Excel и сохраните как «Книга Excel (.xlsx)»."),
            _ => throw new NodeInputException($"«{relative}» — не таблица: подходят файлы .xlsx, .xlsm, .csv и .tsv."),
        };
    }

    public TableSnapshot Read(string? sheet)
    {
        if (Directory.Exists(FullPath))
        {
            throw new NodeInputException($"«{Relative}» — папка, а не таблица.");
        }

        if (!File.Exists(FullPath))
        {
            throw new NodeInputException($"Таблица «{Relative}» не найдена.");
        }

        byte[] bytes;
        (long, DateTime) stamp;
        // Чтение не мешает Excel, в котором таблица может быть открыта.
        using (var stream = new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > MaxBytes)
            {
                throw new NodeInputException($"Таблица «{Relative}» больше 10 МБ ({stream.Length / 1024 / 1024} МБ) — разделите её на части.");
            }

            stamp = Stamp();
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        if (IsCsv)
        {
            var csv = CsvDocument.Parse(bytes, Path.GetExtension(FullPath));
            return new TableSnapshot(csv.ToSheet(), bytes, stamp, csv);
        }

        return new TableSnapshot(XlsxFormat.Read(bytes, sheet, Relative), bytes, stamp, null);
    }

    /// <summary>Пустой лист нового файла (файла ещё нет).</summary>
    public TableSheet New(string? sheet) => IsCsv
        ? new TableSheet { Name = "", Csv = CsvDialect.New }
        : new TableSheet { Name = XlsxFormat.NewSheetName(sheet) };

    /// <summary>
    /// Записать изменённые ячейки (строка → колонка → значение; null — очистить). <paramref name="snapshot"/> — то, что
    /// прочитано перед изменением (null — создать новый файл). Если файл с тех пор изменился, открыт в Excel или занят —
    /// <see cref="TableBusyException"/>, и файл не тронут.
    /// </summary>
    public void Write(TableSnapshot? snapshot, string? sheet, IReadOnlyDictionary<int, IReadOnlyDictionary<int, JsonNode?>> changes,
        int headerRow)
    {
        EnsureNotOpenElsewhere();
        var data = IsCsv
            ? (snapshot?.Csv ?? CsvDocument.Empty(CsvDialect.New)).With(changes)
            : XlsxFormat.Write(snapshot?.Bytes, sheet, changes, headerRow, Relative);

        Directory.CreateDirectory(Path.GetDirectoryName(FullPath)!);
        var temp = Path.Combine(Path.GetDirectoryName(FullPath)!, $"{FileState.TempMarker}{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            if (snapshot is null ? File.Exists(FullPath) : !File.Exists(FullPath) || Stamp() != snapshot.Stamp)
            {
                throw new TableBusyException($"Таблица «{Relative}» изменилась, пока Pepa её записывала.");
            }

            EnsureNotOpenElsewhere();
            // Одно переименование: на Windows не проходит, пока файл открыт программой без разрешения на замену.
            File.Move(temp, FullPath, overwrite: snapshot is not null);
        }
        catch (IOException ex)
        {
            throw new TableBusyException($"Не удалось записать таблицу «{Relative}»: {ex.Message} Возможно, она открыта в другой программе.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new NodeInputException($"Нет права изменить «{Relative}» (файл только для чтения или защищён).");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Временный файл помечен и не попадает в списки файлов.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Таблица открыта в Excel или LibreOffice (рядом их файл-метка): Excel при сохранении сотрёт то, что запишет Pepa,
    /// поэтому записи не будет, пока таблицу не закроют.
    /// </summary>
    private void EnsureNotOpenElsewhere()
    {
        var folder = Path.GetDirectoryName(FullPath)!;
        var name = Path.GetFileName(FullPath);
        foreach (var marker in (string[])[$"~${name}", $".~lock.{name}#"])
        {
            if (File.Exists(Path.Combine(folder, marker)))
            {
                throw new TableBusyException(
                    $"Таблица «{Relative}» открыта в Excel или другой программе (рядом лежит «{marker}»). Закройте её — иначе " +
                    $"программа при сохранении сотрёт то, что запишет Pepa. Если программа уже закрыта, а «{marker}» остался после сбоя, удалите его.");
            }
        }
    }

    private (long, DateTime) Stamp()
    {
        var info = new FileInfo(FullPath);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Изменения одного файла внутри Pepa — по очереди: два процесса, дописывающие строки в одну таблицу, не затрут друг
    /// друга. null — очереди не дождались (процесс остановили): ничего не записано.
    /// </summary>
    public async Task<IDisposable?> LockAsync(CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(FullPath, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
