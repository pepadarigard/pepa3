using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.FileSystem;

/// <summary>Папка, за которой следит Pepa: новые (и, по желанию, изменённые) файлы становятся событиями «files».</summary>
public sealed record FolderWatchSettings
{
    public string Id { get; init; } = "";

    /// <summary>Папка внутри рабочей папки, путь с «/».</summary>
    public string Folder { get; init; } = "";

    /// <summary>Какие файлы: «*.pdf; отчёт-*.xlsx»; «*» — все.</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Следить и за вложенными папками.</summary>
    public bool Subfolders { get; init; }

    /// <summary>Сообщать и об изменённых файлах (а не только о новых).</summary>
    public bool Changes { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>События о файлах: источник «files», вид «created» или «changed», ключ — папка слежения.</summary>
public static class FileEvents
{
    public const string Source = "files";
    public const string Created = "created";
    public const string Changed = "changed";
}

/// <summary>Шаблон имени файла: «*» — любые символы, «?» — один символ, несколько шаблонов — через «;».</summary>
public static class FileNamePattern
{
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '|', '/', '\\'];

    public static IReadOnlyList<string> Split(string? pattern) =>
        (pattern ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>null — шаблон годится; иначе — почему нет.</summary>
    public static string? Validate(string? pattern)
    {
        var parts = Split(pattern);
        if (parts.Count == 0)
        {
            return "Укажите, какие файлы: например «*.pdf» или «*» — все.";
        }

        return parts.FirstOrDefault(p => p.IndexOfAny(Forbidden) >= 0 || p.Any(char.IsControl)) is { } bad
            ? $"Шаблон «{bad}» — только имя файла, без папок и символов < > : \" | / \\."
            : null;
    }

    /// <summary>Регулярное выражение для имени файла (без учёта регистра, как в Windows).</summary>
    public static string RegexSource(string pattern) =>
        "^(?:" + string.Join("|", Split(pattern).Select(p => Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", "."))) + ")$";

    public static Regex ToRegex(string pattern) =>
        new(RegexSource(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

public sealed record FolderWatchStatus(string Id, string Folder, bool Enabled, int Files, int Pending, DateTimeOffset? LastScanAt, string? Error, string? Note);

public sealed record FolderWatcherOptions
{
    /// <summary>Как часто проверять папки.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Файл считается записанным, если столько времени не менялся (и его можно открыть для чтения).</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Больше файлов в одной папке слежения — слежение за ней останавливается с ошибкой.</summary>
    public int MaxFiles { get; init; } = 20_000;
}

/// <summary>
/// Слежение за папками рабочей папки: файл, который появился (или изменился), становится событием, когда он дописан —
/// не менялся <see cref="FolderWatcherOptions.Settle"/> и открывается для чтения. Папки проверяются раз в
/// <see cref="FolderWatcherOptions.Interval"/>.
/// <para>
/// Что уже было в папке, когда слежение включили, — не событие. Список известных файлов хранится на диске: после
/// перезапуска файлы, появившиеся, пока Pepa была закрыта, тоже становятся событиями, но со временем «до запуска» — их
/// получают ожидающие процессы, а процессы по ним сами не запускаются. Повторно о том же файле (то же имя, размер и
/// время изменения) Pepa не сообщает: защита от повторов — по ключу события.
/// </para>
/// </summary>
public sealed class FolderWatcher : IAsyncDisposable
{
    /// <summary>Временные файлы, которые программы создают во время записи (итоговый файл появится под своим именем).</summary>
    private static readonly Regex Temporary = new(@"^(~\$.*|\.~lock\..*|.*\.(tmp|temp|crdownload|part|partial|download|opdownload))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IWorkspace _workspace;
    private readonly string _stateDirectory;
    private readonly Func<EventInput, CancellationToken, Task> _publish;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly FolderWatcherOptions _options;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, WatchState> _states = new(StringComparer.Ordinal);
    private IReadOnlyList<FolderWatchSettings> _watches = [];
    private IReadOnlyList<FolderWatchStatus> _status = [];
    private DateTimeOffset? _startedAt;
    private bool _loadedFromDisk;
    private bool _catchingUp;
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    public FolderWatcher(IWorkspace workspace, string stateDirectory, Func<EventInput, CancellationToken, Task> publish, TimeProvider time,
        ILogger logger, FolderWatcherOptions? options = null)
    {
        _workspace = workspace;
        _stateDirectory = stateDirectory;
        _publish = publish;
        _time = time;
        _logger = logger;
        _options = options ?? new FolderWatcherOptions();
    }

    /// <summary>Состояние слежения (для «Настроек»).</summary>
    public event Action? Changed;

    /// <summary>Файлы, появившиеся, пока Pepa была закрыта, переданы (или их уже нет).</summary>
    public event Action? CaughtUp;

    public IReadOnlyList<FolderWatchSettings> Watches => Volatile.Read(ref _watches);

    public IReadOnlyList<FolderWatchStatus> Status => Volatile.Read(ref _status);

    /// <summary>Будет ли после запуска досмотр того, что появилось без Pepa.</summary>
    public bool WillCatchUp => Watches.Any(w => w.Enabled && File.Exists(SnapshotPath(w.Id)));

    /// <summary>Какие папки отслеживать. Новая или изменённая папка начинает с того, что в ней уже есть.</summary>
    public void Configure(IReadOnlyList<FolderWatchSettings> watches)
    {
        Volatile.Write(ref _watches, watches.ToList());
        Wake();
    }

    /// <param name="engineStartedAt">Когда запущен движок: файлы, появившиеся без Pepa, получают время раньше этого.</param>
    /// <param name="runLoop">false — проверки только через <see cref="ScanAsync(CancellationToken)"/> (для проверок Pepa).</param>
    public void Start(DateTimeOffset engineStartedAt, bool runLoop = true)
    {
        _startedAt = engineStartedAt;
        _catchingUp = true;
        if (runLoop)
        {
            _loop = Task.Run(LoopAsync);
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(_stop.Token);
                await _wake.WaitAsync(_options.Interval, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Слежение продолжится со следующей проверки; ошибка видна в «Настройках».
                _logger.LogWarning(ex, "Ошибка слежения за папками.");
                try
                {
                    await Task.Delay(_options.Interval, _time, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Одна проверка всех папок.</summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        bool changed;
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            Reconcile();
            foreach (var state in _states.Values)
            {
                if (state.Settings.Enabled)
                {
                    await ScanAsync(state, cancellationToken);
                }
            }

            var status = Watches.Select(w => _states.TryGetValue(w.Id, out var s)
                ? new FolderWatchStatus(w.Id, w.Folder, w.Enabled, s.Known?.Count ?? 0, s.Pending.Count, s.LastScanAt, s.Error, s.Note)
                : new FolderWatchStatus(w.Id, w.Folder, w.Enabled, 0, 0, null, null, null)).ToList();
            // Сообщать только о переменах (время проверки меняется каждые пару секунд).
            changed = !status.Select(x => x with { LastScanAt = null }).SequenceEqual(Status.Select(x => x with { LastScanAt = null }));
            Volatile.Write(ref _status, status);
            if (_catchingUp && !_states.Values.Any(s => s.Pending.Values.Any(p => p.CatchUp)))
            {
                _catchingUp = false;
                Raise(CaughtUp);
            }
        }
        finally
        {
            _scanLock.Release();
        }

        if (changed)
        {
            Raise(Changed);
        }
    }

    /// <summary>Состояния — по текущим настройкам: убранные и выключенные забываются, новые начинают с нуля.</summary>
    private void Reconcile()
    {
        var watches = Watches;
        foreach (var id in _states.Keys.Where(id => watches.All(w => w.Id != id || !w.Enabled)).ToList())
        {
            _states.Remove(id);
            DeleteSnapshot(id);
        }

        foreach (var watch in watches.Where(w => !w.Enabled && !_states.ContainsKey(w.Id)))
        {
            DeleteSnapshot(watch.Id);
        }

        foreach (var watch in watches.Where(w => w.Enabled))
        {
            if (_states.TryGetValue(watch.Id, out var state))
            {
                state.Settings = watch;
                continue;
            }

            state = new WatchState(watch);
            // Только при запуске Pepa: досмотреть, что появилось без неё. Включённое позже начинает с того, что есть.
            if (!_loadedFromDisk)
            {
                LoadSnapshot(state);
            }

            _states[watch.Id] = state;
        }

        if (!_loadedFromDisk)
        {
            // Списки слежений, которых больше нет.
            foreach (var orphan in Directory.Exists(_stateDirectory) ? Directory.GetFiles(_stateDirectory, "*.json") : [])
            {
                if (!_states.ContainsKey(Path.GetFileNameWithoutExtension(orphan)))
                {
                    DeleteSnapshot(Path.GetFileNameWithoutExtension(orphan));
                }
            }
        }

        _loadedFromDisk = true;
    }

    private async Task ScanAsync(WatchState state, CancellationToken cancellationToken)
    {
        var settings = state.Settings;
        var now = _time.GetUtcNow();
        state.LastScanAt = now;
        string root;
        string folder;
        try
        {
            root = _workspace.Root;
            folder = _workspace.Resolve(settings.Folder);
        }
        catch (NodeInputException ex)
        {
            state.Error = ex.Message;
            return;
        }

        if (!Directory.Exists(folder))
        {
            state.Error = $"Папки «{settings.Folder}» нет в рабочей папке.";
            state.WasMissing = true;
            return;
        }

        Dictionary<string, FileSeen> listing;
        try
        {
            listing = List(folder, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TooManyFilesException)
        {
            state.Error = ex is TooManyFilesException
                ? $"В папке больше {_options.MaxFiles} подходящих файлов — слежение за ней не работает. Уточните шаблон или папку."
                : $"Не удалось прочитать папку: {ex.Message}";
            return;
        }

        state.Error = null;
        var key = $"{root}\n{settings.Folder}\n{settings.Pattern}\n{settings.Subfolders}";
        if (state.Known is null || state.Key != key || state.WasMissing)
        {
            // Начало слежения (или папка снова доступна): то, что уже лежит в папке, событием не считается.
            state.Note = state.WasMissing && state.Known is not null
                ? $"Папка была недоступна; файлы, появившиеся за это время, не считаются новыми (с {FormatLocal(now)})."
                : null;
            state.Known = listing.ToDictionary(p => p.Key, p => p.Value.Version, StringComparer.Ordinal);
            state.Key = key;
            state.WasMissing = false;
            state.Pending.Clear();
            SaveSnapshot(state);
            return;
        }

        var dirty = false;
        foreach (var path in state.Known.Keys.Where(p => !listing.ContainsKey(p)).ToList())
        {
            state.Known.Remove(path);
            dirty = true;
        }

        foreach (var path in state.Pending.Keys.Where(p => !listing.ContainsKey(p)).ToList())
        {
            state.Pending.Remove(path);
        }

        foreach (var (path, seen) in listing)
        {
            var known = state.Known.TryGetValue(path, out var version) ? version : (FileVersion?)null;
            if (known == seen.Version)
            {
                state.Pending.Remove(path);
                continue;
            }

            if (known is not null && !settings.Changes)
            {
                state.Known[path] = seen.Version;
                state.Pending.Remove(path);
                dirty = true;
                continue;
            }

            if (!state.Pending.TryGetValue(path, out var pending) || pending.Version != seen.Version)
            {
                // Новый или ещё меняется: ждём, пока перестанет.
                state.Pending[path] = new PendingFile(seen.Version, pending?.FirstSeen ?? now, now, pending?.CatchUp ?? state.FirstScan);
                continue;
            }

            if (now - pending.StableSince < _options.Settle || !CanRead(seen.FullPath))
            {
                continue;
            }

            var input = Event(settings, path, seen, known is null ? FileEvents.Created : FileEvents.Changed, pending);
            try
            {
                await _publish(input, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Событие не передано — попробуем при следующей проверке (повтор того же файла Pepa распознает).
                state.Error = $"Не удалось передать событие о файле «{path}»: {ex.Message}";
                _logger.LogWarning(ex, "Не удалось передать событие о файле {Path}.", path);
                break;
            }

            state.Known[path] = seen.Version;
            state.Pending.Remove(path);
            dirty = true;
        }

        if (dirty)
        {
            SaveSnapshot(state);
        }

        state.FirstScan = false;
    }

    private EventInput Event(FolderWatchSettings settings, string path, FileSeen seen, string type, PendingFile pending)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        // Появился, пока Pepa была закрыта, — время раньше запуска: процессы по такому событию сами не запускаются.
        var occurredAt = pending.CatchUp && _startedAt is { } started
            ? (seen.AppearedAt < started ? seen.AppearedAt : started.AddMilliseconds(-1))
            : pending.FirstSeen;
        return new EventInput
        {
            Source = FileEvents.Source,
            Type = type,
            CorrelationKey = settings.Folder,
            ExternalId = $"{settings.Id}:{path}:{seen.Version.Size}:{seen.Version.Ticks}",
            OccurredAt = occurredAt,
            Payload = new JsonObject
            {
                ["path"] = path,
                ["name"] = name,
                ["folder"] = settings.Folder,
                ["extension"] = Path.GetExtension(name).ToLowerInvariant(),
                ["size"] = seen.Version.Size,
                ["modifiedAt"] = new DateTimeOffset(seen.Version.Ticks, TimeSpan.Zero).ToString("O"),
            },
        };
    }

    private Dictionary<string, FileSeen> List(string folder, FolderWatchSettings settings)
    {
        var pattern = FileNamePattern.ToRegex(settings.Pattern);
        var result = new Dictionary<string, FileSeen>(StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.Subfolders,
            // Ссылки (symlink, junction) не проходим: они могут вести за пределы рабочей папки.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
            IgnoreInaccessible = true,
        };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            if (!pattern.IsMatch(file.Name) || Temporary.IsMatch(file.Name))
            {
                continue;
            }

            if (result.Count >= _options.MaxFiles)
            {
                throw new TooManyFilesException();
            }

            var relative = settings.Folder + "/" + Path.GetRelativePath(folder, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            var written = file.LastWriteTimeUtc;
            var created = file.CreationTimeUtc;
            result[relative] = new FileSeen(new FileVersion(file.Length, written.Ticks),
                new DateTimeOffset(created > written ? created : written, TimeSpan.Zero), file.FullName);
        }

        return result;
    }

    /// <summary>Файл дописан: его можно открыть для чтения, не разрешая другим запись (не получится, пока его пишут).</summary>
    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────── Список известных файлов на диске ─────────────

    private string SnapshotPath(string id) => Path.Combine(_stateDirectory, $"{id}.json");

    private void LoadSnapshot(WatchState state)
    {
        var path = SnapshotPath(state.Settings.Id);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var files = new Dictionary<string, FileVersion>(StringComparer.Ordinal);
            foreach (var (file, value) in json?["files"] as JsonObject ?? [])
            {
                if (value is JsonArray { Count: 2 } pair)
                {
                    files[file] = new FileVersion(pair[0]!.GetValue<long>(), pair[1]!.GetValue<long>());
                }
            }

            state.Key = json?["key"]?.GetValue<string>();
            state.Known = files;
            state.FirstScan = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            // Список повреждён — слежение начнётся заново (без событий о том, что уже есть), и об этом сказано.
            state.Note = $"Список известных файлов не прочитан ({ex.Message}) — слежение начато заново.";
            _logger.LogWarning(ex, "Не удалось прочитать список файлов слежения {Id}.", state.Settings.Id);
        }
    }

    private void SaveSnapshot(WatchState state)
    {
        var files = new JsonObject();
        foreach (var (file, version) in state.Known!)
        {
            files[file] = new JsonArray(version.Size, version.Ticks);
        }

        try
        {
            Directory.CreateDirectory(_stateDirectory);
            FileState.WriteAtomically(SnapshotPath(state.Settings.Id),
                Encoding.UTF8.GetBytes(new JsonObject { ["key"] = state.Key, ["files"] = files }.ToJsonString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Без списка на диске после перезапуска Pepa сообщит о тех же файлах повторно — их отсечёт защита от повторов.
            state.Error = $"Не удалось сохранить список известных файлов: {ex.Message}";
            _logger.LogWarning(ex, "Не удалось сохранить список файлов слежения {Id}.", state.Settings.Id);
        }
    }

    private void DeleteSnapshot(string id)
    {
        try
        {
            File.Delete(SnapshotPath(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось удалить список файлов слежения {Id}.", id);
        }
    }

    private static string FormatLocal(DateTimeOffset at) => at.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка подписчика слежения за папками.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Слежение за папками остановилось с ошибкой.");
        }

        _stop.Dispose();
    }

    private readonly record struct FileVersion(long Size, long Ticks);

    private readonly record struct FileSeen(FileVersion Version, DateTimeOffset AppearedAt, string FullPath);

    /// <param name="StableSince">С какой проверки размер и время изменения не менялись.</param>
    /// <param name="CatchUp">Файл замечен при первой проверке после запуска Pepa (мог появиться без неё).</param>
    private sealed record PendingFile(FileVersion Version, DateTimeOffset FirstSeen, DateTimeOffset StableSince, bool CatchUp);

    private sealed class WatchState(FolderWatchSettings settings)
    {
        public FolderWatchSettings Settings { get; set; } = settings;
        public string? Key { get; set; }
        public Dictionary<string, FileVersion>? Known { get; set; }
        public Dictionary<string, PendingFile> Pending { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? LastScanAt { get; set; }
        public string? Error { get; set; }
        public string? Note { get; set; }
        public bool WasMissing { get; set; }

        /// <summary>Первая проверка после запуска Pepa по списку с диска: найденное могло появиться без Pepa.</summary>
        public bool FirstScan { get; set; }
    }

    private sealed class TooManyFilesException : Exception;
}
