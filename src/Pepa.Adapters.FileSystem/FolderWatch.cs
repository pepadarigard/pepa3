using System.Diagnostics;
using System.Text;
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

    /// <summary>Части шаблона. Разделитель — только «;» (запятая бывает в именах файлов: «Ромашка, ООО.pdf»).</summary>
    public static IReadOnlyList<string> Split(string? pattern) =>
        (pattern ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    /// <summary>Больше подходящих файлов в одной папке слежения — слежение за ней не работает (ошибка).</summary>
    public int MaxFiles { get; init; } = 20_000;

    /// <summary>Больше файлов и папок вообще (подходящих и нет) — тоже ошибка: проверка была бы слишком дорогой.</summary>
    public int MaxEntries { get; init; } = 200_000;

    /// <summary>Как часто сохранять список известных файлов (и при остановке).</summary>
    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Слежение за папками рабочей папки: файл, который появился (или изменился), становится событием, когда он дописан —
/// не менялся <see cref="FolderWatcherOptions.Settle"/>, открывается для чтения и рядом нет его временного файла
/// (скачивание ещё идёт). Папки проверяются раз в <see cref="FolderWatcherOptions.Interval"/>.
/// <para>
/// Что уже было в папке, когда слежение включили, — не событие. Переименованный или перенесённый внутри папки файл (то же
/// содержимое, время создания и изменения) — тоже не событие. Список известных файлов хранится на диске: после
/// перезапуска файлы, появившиеся, пока Pepa была закрыта, тоже становятся событиями, но со временем «до запуска» — их
/// получают ожидающие процессы, а процессы по ним сами не запускаются. Повторно о том же файле (то же имя, размер и
/// время изменения) Pepa не сообщает: защита от повторов — по ключу события.
/// </para>
/// </summary>
public sealed class FolderWatcher : IAsyncDisposable
{
    /// <summary>Временные файлы, которые программы создают во время записи (итоговый файл появится под своим именем).</summary>
    private static readonly Regex Temporary = new(
        @"^(~\$.*|\.~lock\..*|.*\.(tmp|temp|crdownload|part|partial|download|opdownload|filepart|!qb|!ut|aria2))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Окончания временного файла рядом с итоговым: пока он есть, итоговый файл не дописан (Firefox, торренты…).</summary>
    private static readonly string[] TemporarySuffixes = [".part", ".crdownload", ".partial", ".download", ".opdownload", ".filepart", ".!qb", ".!ut", ".aria2", ".tmp"];

    /// <summary>Пустой файл часто — заготовка, в которую программа ещё запишет (так скачивает Firefox): ждём дольше.</summary>
    private const int EmptySettleFactor = 10;

    /// <summary>Удалённые файлы помнятся сутки: вернувшийся без изменений (папку скрыли и показали снова) — не новый.</summary>
    private static readonly TimeSpan TombstoneLife = TimeSpan.FromDays(1);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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

    /// <summary>Когда Pepa в прошлый раз проверяла папки (перед закрытием): файл, найденный после запуска, появился позже.</summary>
    private DateTimeOffset? _checkedBefore;

    private DateTimeOffset _checkedSavedAt;
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
    public bool WillCatchUp => Watches.Any(w => w.Enabled && (File.Exists(SnapshotPath(w.Id)) || File.Exists(LegacySnapshotPath(w.Id))));

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
        _checkedBefore = LoadCheckedAt();
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
                // Слежение продолжится со следующей проверки (ошибки отдельных папок видны в «Настройках»).
                _logger.LogWarning(ex, "Ошибка слежения за папками.");
                try
                {
                    await Task.Delay(_options.Interval, _stop.Token);
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
            var watches = Watches;
            Reconcile(watches);
            var now = _time.GetUtcNow();
            foreach (var state in _states.Values)
            {
                if (state.NextScanTimestamp > _time.GetTimestamp())
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                try
                {
                    await ScanAsync(state, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    state.Error = $"Не удалось проверить папку: {ex.Message}";
                    _logger.LogWarning(ex, "Ошибка слежения за папкой {Folder}.", state.Settings.Folder);
                }

                // Большое дерево папок проверяется реже, чтобы проверка не занимала диск постоянно.
                var took = Stopwatch.GetElapsedTime(started);
                state.NextScanTimestamp = took > TimeSpan.FromMilliseconds(200)
                    ? _time.GetTimestamp() + (long)(Math.Min(30, took.TotalSeconds * 10) * _time.TimestampFrequency)
                    : 0;
                SaveIfDue(state, now);
            }

            SaveCheckedAt(now, force: false);
            var status = watches.Select(w => _states.TryGetValue(w.Id, out var s)
                ? new FolderWatchStatus(w.Id, w.Folder, w.Enabled, s.Known?.Count ?? 0, s.Pending.Count, s.LastScanAt, s.Error, s.Note)
                : new FolderWatchStatus(w.Id, w.Folder, w.Enabled, 0, 0, null, null, null)).ToList();
            // Сообщать только о переменах (время проверки меняется каждые пару секунд).
            changed = !status.Select(x => x with { LastScanAt = null }).SequenceEqual(Status.Select(x => x with { LastScanAt = null }));
            Volatile.Write(ref _status, status);
            if (_catchingUp && !_states.Values.Any(s => s.FirstScan || s.Pending.Values.Any(p => p.CatchUp)))
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
    private void Reconcile(IReadOnlyList<FolderWatchSettings> watches)
    {
        foreach (var id in _states.Keys.Where(id => watches.All(w => w.Id != id || !w.Enabled)).ToList())
        {
            _states.Remove(id);
            DeleteSnapshot(id);
        }

        foreach (var watch in watches.Where(w => !w.Enabled))
        {
            DeleteSnapshot(watch.Id);
        }

        foreach (var watch in watches.Where(w => w.Enabled))
        {
            if (_states.TryGetValue(watch.Id, out var state))
            {
                if (state.Settings != watch)
                {
                    // Настройки изменились — проверить сразу, не дожидаясь отложенной проверки большой папки.
                    state.NextScanTimestamp = 0;
                }

                state.Settings = watch;
                continue;
            }

            state = new WatchState(watch);
            // Только при запуске Pepa: досмотреть, что появилось без неё. Включённое позже начинает с того, что есть.
            if (!_loadedFromDisk)
            {
                MigrateLegacySnapshot(watch.Id);
                LoadSnapshot(state);
            }

            _states[watch.Id] = state;
        }

        if (!_loadedFromDisk)
        {
            // Списки слежений, которых больше нет.
            try
            {
                foreach (var orphan in Directory.Exists(_stateDirectory) ? Directory.GetFiles(_stateDirectory, "*.json") : [])
                {
                    var name = Path.GetFileNameWithoutExtension(orphan);
                    var id = name.StartsWith("watch-", StringComparison.Ordinal) ? name["watch-".Length..] : name;
                    if (name != "checked" && !_states.ContainsKey(id))
                    {
                        File.Delete(orphan);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Не удалось убрать старые списки слежения.");
            }
        }

        _loadedFromDisk = true;
    }

    private async Task ScanAsync(WatchState state, CancellationToken cancellationToken)
    {
        var settings = state.Settings;
        var now = _time.GetUtcNow();
        state.LastScanAt = now;
        // Рабочая папка — одна на всю проверку: если её сменят посреди проверки, новые файлы не сравнятся со старым списком.
        var root = _workspace.Root;
        string folder;
        try
        {
            folder = new Workspace(root).Resolve(settings.Folder);
        }
        catch (NodeInputException ex)
        {
            // Как пропавшая папка: когда путь снова годится, слежение начнётся с того, что в папке будет.
            state.Error = ex.Message;
            state.WasMissing = true;
            state.FirstScan = false;
            return;
        }

        if (!Directory.Exists(folder))
        {
            // Когда папка вернётся, слежение начнётся с того, что в ней будет, — досматривать нечего.
            state.Error = $"Папки «{settings.Folder}» нет в рабочей папке.";
            state.WasMissing = true;
            state.FirstScan = false;
            return;
        }

        Listing listing;
        try
        {
            listing = List(folder, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TooManyFilesException)
        {
            state.Error = ex is TooManyFilesException many
                ? many.AllEntries
                    ? $"В папке (с вложенными) больше {_options.MaxEntries} файлов и папок — слежение за ней не работает. Выберите папку поменьше."
                    : $"В папке больше {_options.MaxFiles} подходящих файлов — слежение за ней не работает. Уточните шаблон или папку."
                : $"Не удалось прочитать папку: {ex.Message}";
            return;
        }

        state.Error = state.SaveFailed ? state.Error : null;
        var key = $"{root}\n{settings.Folder}\n{settings.Pattern}\n{settings.Subfolders}";
        if (state.Known is null || state.Key != key || state.WasMissing)
        {
            // Начало слежения (или папка снова доступна): то, что уже лежит в папке, событием не считается.
            state.ResetNote = state.LoadProblem ?? (state.WasMissing && state.Known is not null
                ? $"Папка была недоступна; файлы, появившиеся за это время, не считаются новыми (с {FormatLocal(now)})."
                : null);
            state.LoadProblem = null;
            state.Known = listing.Files.ToDictionary(p => p.Key, p => p.Value.Version, StringComparer.Ordinal);
            state.Key = key;
            state.WasMissing = false;
            state.FirstScan = false;
            state.Pending.Clear();
            state.Tombstones.Clear();
            // Нечитаемые вложенные папки — тоже часть того, что уже есть: когда их прочитают, их файлы не новые.
            state.Skipped = listing.Skipped;
            state.Note = Notes(state, listing);
            state.Dirty = true;
            state.SaveNow = true;
            return;
        }

        // Вложенные папки, которые в прошлый раз не читались, а теперь читаются: их файлы не новые.
        var reappeared = state.Skipped.Where(f => !listing.Skipped.Contains(f)).ToList();

        // Исчезнувшие файлы (кроме лежащих в папках, которые сейчас не читаются).
        var gone = state.Known.Where(p => !listing.Files.ContainsKey(p.Key) && !listing.IsSkipped(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        // Откуда мог прийти переименованный файл: исчезнувшие и заменённые (x → x.bak, а на месте x — новый файл).
        var moved = new Dictionary<FileVersion, Queue<string>>();
        foreach (var (path, version) in state.Known)
        {
            var replaced = listing.Files.TryGetValue(path, out var current) && !current.Version.SameContent(version);
            if (version.Created != 0 && (gone.ContainsKey(path) || replaced))
            {
                (moved.TryGetValue(version, out var queue) ? queue : moved[version] = new Queue<string>()).Enqueue(path);
            }
        }

        // Ожидающие записи, которых нет на месте (их переименовали): пометка «появился без Pepa» переходит к новому имени.
        var lostPending = new Dictionary<FileVersion, Queue<PendingFile>>();
        foreach (var path in state.Pending.Keys.Where(p => !listing.Files.ContainsKey(p) && !listing.IsSkipped(p)).ToList())
        {
            var lost = state.Pending[path];
            (lostPending.TryGetValue(lost.Version, out var queue) ? queue : lostPending[lost.Version] = new Queue<PendingFile>()).Enqueue(lost);
            state.Pending.Remove(path);
        }

        var canPublish = true;
        var saveNow = false;
        var absorbed = 0;
        foreach (var (path, seen) in listing.Files)
        {
            var known = state.Known.TryGetValue(path, out var version) ? version : (FileVersion?)null;
            if (known is null)
            {
                // Переименован или перенесён внутри папки (то же содержимое, время создания и изменения) — не новый.
                if (moved.TryGetValue(seen.Version, out var from) && from.TryDequeue(out var oldPath))
                {
                    if (gone.Remove(oldPath))
                    {
                        state.Known.Remove(oldPath);
                    }

                    state.Known[path] = seen.Version;
                    state.Dirty = true;
                    saveNow = true;
                    continue;
                }

                // Пропадал и вернулся без изменений (папку скрывали) — тоже не новый.
                if (state.Tombstones.TryGetValue(path, out var tombstone) && tombstone.Version.SameContent(seen.Version))
                {
                    state.Tombstones.Remove(path);
                    state.Known[path] = seen.Version;
                    state.Dirty = true;
                    continue;
                }

                if (seen.Quiet || reappeared.Any(f => path.StartsWith(f + "/", StringComparison.Ordinal)))
                {
                    state.Known[path] = seen.Version;
                    state.Dirty = true;
                    absorbed += seen.Quiet ? 0 : 1;
                    continue;
                }
            }

            if (known is { } k && k.SameContent(seen.Version))
            {
                state.Pending.Remove(path);
                continue;
            }

            if (known is not null && (!settings.Changes || seen.Quiet))
            {
                state.Known[path] = seen.Version;
                state.Pending.Remove(path);
                state.Dirty = true;
                continue;
            }

            if (!state.Pending.TryGetValue(path, out var pending) || !pending.Version.SameContent(seen.Version))
            {
                if (pending is null && lostPending.TryGetValue(seen.Version, out var renamed) && renamed.TryDequeue(out var carried))
                {
                    state.Pending[path] = carried;
                    continue;
                }

                // Новый или ещё меняется: ждём, пока перестанет.
                state.Pending[path] = new PendingFile(seen.Version, pending?.FirstSeen ?? now, now, pending?.CatchUp ?? state.FirstScan);
                continue;
            }

            var settle = seen.Version.Size == 0 ? _options.Settle * EmptySettleFactor : _options.Settle;
            if (!canPublish || now - pending.StableSince < settle || listing.HasTemporaryNear(seen.FullPath) || !CanRead(seen.FullPath))
            {
                continue;
            }

            if (!string.Equals(_workspace.Root, root, StringComparison.Ordinal))
            {
                // Рабочую папку сменили во время проверки — следующая проверка начнёт слежение в новой.
                return;
            }

            var input = Event(settings, path, seen, known is null ? FileEvents.Created : FileEvents.Changed, pending);
            try
            {
                await _publish(input, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Событие не передано — попробуем при следующей проверке (повтор того же файла Pepa распознает). Остальные
                // файлы этой проверки разбираются как обычно (переименования), но событий больше не передаём.
                state.Error = $"Не удалось передать событие о файле «{path}»: {ex.Message}";
                _logger.LogWarning(ex, "Не удалось передать событие о файле {Path}.", path);
                canPublish = false;
                continue;
            }

            state.Known[path] = seen.Version;
            state.Pending.Remove(path);
            state.Dirty = true;
            saveNow = true;
        }

        foreach (var (path, old) in gone)
        {
            if (state.Known.Remove(path))
            {
                state.Tombstones[path] = new Tombstone(old, now);
                state.Dirty = true;
            }
        }

        TrimTombstones(state, now);
        if (absorbed > 0)
        {
            state.ResetNote = $"Вложенная папка снова читается — её файлы ({absorbed}) не считаются новыми (с {FormatLocal(now)}).";
        }

        state.Skipped = listing.Skipped;
        state.Note = Notes(state, listing);
        // После события или переименования список сохраняется сразу: иначе после сбоя переименованный обработанный файл
        // стал бы «новым» (ключ от повторов включает путь).
        state.SaveNow |= saveNow;
        state.FirstScan = false;
    }

    private static string? Notes(WatchState state, Listing listing)
    {
        var unreadable = listing.Unreadable.Count == 0
            ? null
            : $"Не читаются вложенные папки ({string.Join(", ", listing.Unreadable.Take(3).Select(f => $"«{f}»"))}" +
              (listing.Unreadable.Count > 3 ? $" и ещё {listing.Unreadable.Count - 3}" : "") + ") — их файлы не проверяются.";
        return state.ResetNote is null ? unreadable : unreadable is null ? state.ResetNote : $"{state.ResetNote} {unreadable}";
    }

    /// <summary>Удалённые файлы помнятся не дольше суток и не больше <see cref="FolderWatcherOptions.MaxFiles"/>.</summary>
    private void TrimTombstones(WatchState state, DateTimeOffset now)
    {
        if (now - state.TombstonesSweptAt > TimeSpan.FromMinutes(10))
        {
            state.TombstonesSweptAt = now;
            foreach (var expired in state.Tombstones.Where(t => now - t.Value.At > TombstoneLife).Select(t => t.Key).ToList())
            {
                state.Tombstones.Remove(expired);
            }
        }

        if (state.Tombstones.Count > _options.MaxFiles)
        {
            foreach (var oldest in state.Tombstones.OrderBy(t => t.Value.At).Take(state.Tombstones.Count - _options.MaxFiles).Select(t => t.Key).ToList())
            {
                state.Tombstones.Remove(oldest);
            }
        }
    }

    private EventInput Event(FolderWatchSettings settings, string path, FileSeen seen, string type, PendingFile pending)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        DateTimeOffset occurredAt;
        if (pending.CatchUp && _startedAt is { } started)
        {
            // Появился, пока Pepa была закрыта: позже прошлой проверки (даже если время файла старое — его перенесли) и раньше
            // запуска — процессы по такому событию сами не запускаются, а ожидающие его получат.
            var earliest = _checkedBefore is { } checkedAt ? checkedAt.AddMilliseconds(1) : DateTimeOffset.MinValue;
            var latest = started.AddMilliseconds(-1);
            occurredAt = seen.AppearedAt < earliest ? earliest : seen.AppearedAt;
            occurredAt = occurredAt > latest ? latest : occurredAt;
        }
        else
        {
            // Замечен во время работы Pepa — не раньше запуска (даже если часы компьютера после запуска перевели назад).
            occurredAt = _startedAt is { } start && pending.FirstSeen < start ? start : pending.FirstSeen;
        }

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

    private Listing List(string folder, FolderWatchSettings settings)
    {
        var pattern = FileNamePattern.ToRegex(settings.Pattern);
        var listing = new Listing(settings.Folder);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            // Ссылки (symlink, junction) не проходим: они могут вести за пределы рабочей папки.
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        var entries = 0;
        // Папка и «тихая» ли она: файлы скрытых папок известны Pepa, но событий не дают (когда папку покажут или
        // переименуют, её файлы не новые).
        var folders = new Stack<(string Path, bool Quiet)>();
        folders.Push((folder, false));
        while (folders.TryPop(out var current))
        {
            var directory = current.Path;
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    if (++entries > _options.MaxEntries)
                    {
                        throw new TooManyFilesException(allEntries: true);
                    }

                    var hidden = (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    if (entry is DirectoryInfo subfolder)
                    {
                        if (settings.Subfolders)
                        {
                            folders.Push((subfolder.FullName, current.Quiet || hidden));
                        }

                        continue;
                    }

                    if (hidden)
                    {
                        continue;
                    }

                    var file = (FileInfo)entry;
                    if (Temporary.IsMatch(file.Name) || (file.Attributes & FileAttributes.Temporary) != 0)
                    {
                        listing.Temporary.Add(file.FullName);
                        continue;
                    }

                    if (!pattern.IsMatch(file.Name))
                    {
                        continue;
                    }

                    if (listing.Files.Count >= _options.MaxFiles)
                    {
                        throw new TooManyFilesException(allEntries: false);
                    }

                    var relative = Relative(folder, settings, file.FullName);
                    var written = file.LastWriteTimeUtc;
                    var created = file.CreationTimeUtc;
                    listing.Files[relative] = new FileSeen(new FileVersion(file.Length, written.Ticks, created.Ticks),
                        new DateTimeOffset(created > written ? created : written, TimeSpan.Zero), file.FullName, current.Quiet);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && directory != folder)
            {
                // Вложенная папка не читается (нет доступа, удалили во время проверки): её файлы считаем прежними.
                var relative = Relative(folder, settings, directory);
                listing.Unreadable.Add(relative);
                listing.Skipped.Add(relative);
            }
        }

        return listing;
    }

    private static string Relative(string folder, FolderWatchSettings settings, string fullPath) =>
        settings.Folder + "/" + Path.GetRelativePath(folder, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Файл дописан: его можно открыть для чтения, не разрешая другим запись (не получится, пока его пишут).</summary>
    /// <remarks>На Linux и macOS такая проверка не действует (блокировки там только рекомендательные) — там остаётся только
    /// проверка, что файл перестал меняться.</remarks>
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

    private string SnapshotPath(string id) => Path.Combine(_stateDirectory, $"watch-{id}.json");

    /// <summary>Первая версия слежения называла список «{id}.json».</summary>
    private string LegacySnapshotPath(string id) => Path.Combine(_stateDirectory, $"{id}.json");

    private void MigrateLegacySnapshot(string id)
    {
        try
        {
            if (!File.Exists(SnapshotPath(id)) && File.Exists(LegacySnapshotPath(id)))
            {
                File.Move(LegacySnapshotPath(id), SnapshotPath(id));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось переименовать старый список файлов слежения {Id}.", id);
        }
    }

    private string CheckedPath => Path.Combine(_stateDirectory, "checked.json");

    private void LoadSnapshot(WatchState state)
    {
        var path = SnapshotPath(state.Settings.Id);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            var files = new Dictionary<string, FileVersion>(StringComparer.Ordinal);
            foreach (var (file, value) in (JsonObject)json["files"]!)
            {
                var pair = (JsonArray)value!;
                files[file] = new FileVersion(pair[0]!.GetValue<long>(), pair[1]!.GetValue<long>(), pair.Count > 2 ? pair[2]!.GetValue<long>() : 0);
            }

            state.Key = json["key"]!.GetValue<string>();
            state.Known = files;
            state.FirstScan = true;
        }
        catch (Exception ex)
        {
            // Список повреждён — слежение начнётся заново (без событий о том, что уже есть), и об этом сказано.
            state.Known = null;
            state.LoadProblem = $"Список известных файлов не прочитан ({ex.Message}) — слежение начато заново: файлы, появившиеся, " +
                                "пока Pepa была закрыта, не стали событиями.";
            _logger.LogWarning(ex, "Не удалось прочитать список файлов слежения {Id}.", state.Settings.Id);
        }
    }

    private void SaveIfDue(WatchState state, DateTimeOffset now)
    {
        if (state.Dirty && (state.SaveNow || state.SaveFailed || now - state.SavedAt >= _options.SaveEvery))
        {
            SaveSnapshot(state, now);
        }
    }

    private void SaveSnapshot(WatchState state, DateTimeOffset now)
    {
        if (state.Known is null)
        {
            return;
        }

        var files = new JsonObject();
        foreach (var (file, version) in state.Known)
        {
            files[file] = new JsonArray(version.Size, version.Ticks, version.Created);
        }

        try
        {
            Directory.CreateDirectory(_stateDirectory);
            FileState.WriteAtomically(SnapshotPath(state.Settings.Id),
                Encoding.UTF8.GetBytes(new JsonObject { ["key"] = state.Key, ["files"] = files }.ToJsonString()));
            state.Dirty = false;
            state.SaveNow = false;
            state.SavedAt = now;
            if (state.SaveFailed)
            {
                state.SaveFailed = false;
                state.Error = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Без списка на диске после перезапуска файлы, появившиеся без Pepa, не стали бы событиями — повторяем каждую
            // проверку, и ошибка видна, пока не получится.
            state.SaveFailed = true;
            state.Error = $"Не удалось сохранить список известных файлов: {ex.Message}";
            _logger.LogWarning(ex, "Не удалось сохранить список файлов слежения {Id}.", state.Settings.Id);
        }
    }

    private DateTimeOffset? LoadCheckedAt()
    {
        try
        {
            return File.Exists(CheckedPath) && JsonNode.Parse(File.ReadAllText(CheckedPath))?["checkedAt"]?.GetValue<string>() is { } text &&
                   DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                ? at
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать время прошлой проверки папок.");
            return null;
        }
    }

    private void SaveCheckedAt(DateTimeOffset now, bool force)
    {
        if (_states.Count == 0 || (!force && now - _checkedSavedAt < _options.SaveEvery))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_stateDirectory);
            FileState.WriteAtomically(CheckedPath, Encoding.UTF8.GetBytes(new JsonObject { ["checkedAt"] = now.ToString("O") }.ToJsonString()));
            _checkedSavedAt = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить время проверки папок.");
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

        // Сохранить всё, что ещё не сохранено, и время последней проверки.
        await _scanLock.WaitAsync();
        try
        {
            var now = _time.GetUtcNow();
            foreach (var state in _states.Values.Where(s => s.Dirty))
            {
                SaveSnapshot(state, now);
            }

            if (_startedAt is not null)
            {
                SaveCheckedAt(now, force: true);
            }
        }
        finally
        {
            _scanLock.Release();
        }

        _stop.Dispose();
    }

    /// <param name="Created">Время создания: по нему (вместе с размером и временем изменения) узнаётся переименованный файл.</param>
    private readonly record struct FileVersion(long Size, long Ticks, long Created)
    {
        /// <summary>Содержимое то же (время создания при этом может отличаться: копию положили на место файла).</summary>
        public bool SameContent(FileVersion other) => Size == other.Size && Ticks == other.Ticks;
    }

    /// <param name="Quiet">Файл в скрытой вложенной папке: известен, но событий о нём нет.</param>
    private readonly record struct FileSeen(FileVersion Version, DateTimeOffset AppearedAt, string FullPath, bool Quiet = false);

    /// <param name="StableSince">С какой проверки размер и время изменения не менялись.</param>
    /// <param name="CatchUp">Файл замечен при первой проверке после запуска Pepa (мог появиться без неё).</param>
    private sealed record PendingFile(FileVersion Version, DateTimeOffset FirstSeen, DateTimeOffset StableSince, bool CatchUp);

    private sealed record Tombstone(FileVersion Version, DateTimeOffset At);

    private sealed class Listing(string folder)
    {
        public Dictionary<string, FileSeen> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Временные файлы (полные пути) — по ним видно, что рядом лежащий файл ещё пишется.</summary>
        public HashSet<string> Temporary { get; } = new(PathComparer);

        /// <summary>Вложенные папки, которые не прочитались (путь в рабочей папке).</summary>
        public List<string> Unreadable { get; } = [];

        /// <summary>Вложенные папки, которые не прочитались (путь в рабочей папке) — их файлы считаются прежними.</summary>
        public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);

        public bool HasTemporaryNear(string fullPath) => Temporary.Count > 0 && TemporarySuffixes.Any(s => Temporary.Contains(fullPath + s));

        public bool IsSkipped(string path) =>
            path.StartsWith(folder + "/", StringComparison.Ordinal) && Skipped.Any(u => path.StartsWith(u + "/", StringComparison.Ordinal));
    }

    private sealed class WatchState(FolderWatchSettings settings)
    {
        public FolderWatchSettings Settings { get; set; } = settings;
        public string? Key { get; set; }
        public Dictionary<string, FileVersion>? Known { get; set; }
        public Dictionary<string, PendingFile> Pending { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Tombstone> Tombstones { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? LastScanAt { get; set; }
        /// <summary>Когда проверять снова (по монотонным часам: перевод часов назад не останавливает слежение).</summary>
        public long NextScanTimestamp { get; set; }

        /// <summary>Вложенные папки, которых в прошлую проверку не было видно.</summary>
        public HashSet<string> Skipped { get; set; } = new(StringComparer.Ordinal);

        public DateTimeOffset TombstonesSweptAt { get; set; } = DateTimeOffset.MinValue;
        public string? Error { get; set; }
        public string? Note { get; set; }

        /// <summary>Почему список с диска не прочитан — показывается, когда слежение начнётся заново.</summary>
        public string? LoadProblem { get; set; }

        /// <summary>Почему слежение начато заново (список не прочитан, папка была недоступна) — видно до следующего такого случая.</summary>
        public string? ResetNote { get; set; }

        public bool WasMissing { get; set; }

        /// <summary>Первая проверка после запуска Pepa по списку с диска: найденное могло появиться без Pepa.</summary>
        public bool FirstScan { get; set; }

        public bool Dirty { get; set; }
        public bool SaveNow { get; set; }
        public bool SaveFailed { get; set; }
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class TooManyFilesException(bool allEntries) : Exception
    {
        public bool AllEntries { get; } = allEntries;
    }
}
