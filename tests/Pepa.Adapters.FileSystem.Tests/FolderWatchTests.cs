using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.FileSystem.Tests;

/// <summary>Слежение за папками: новый файл — одно событие, когда он дописан; что уже было — не событие.</summary>
public sealed class FolderWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pepa-watch-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly List<EventInput> _events = [];

    public FolderWatchTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "workspace", "входящие"));
    }

    private string Workspace => Path.Combine(_dir, "workspace");

    private string Inbox(string name) => Path.Combine(Workspace, "входящие", name);

    private FolderWatcher Create(FolderWatcherOptions? options = null, Func<EventInput, Task>? publish = null)
    {
        var watcher = new FolderWatcher(new Workspace(Workspace), Path.Combine(_dir, "watches"),
            (input, _) => publish?.Invoke(input) ?? Record(input), _time, NullLogger.Instance, options);
        return watcher;
    }

    private Task Record(EventInput input)
    {
        lock (_events)
        {
            _events.Add(input);
        }

        return Task.CompletedTask;
    }

    private static FolderWatchSettings Watch(string pattern = "*", bool changes = false, bool subfolders = false, string folder = "входящие") =>
        new() { Id = "w1", Folder = folder, Pattern = pattern, Changes = changes, Subfolders = subfolders };

    /// <summary>Проверка, затем ещё одна после того, как файлы «отлежались».</summary>
    private async Task SettleAsync(FolderWatcher watcher)
    {
        await watcher.ScanAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(3));
        await watcher.ScanAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Files_that_were_there_are_not_events_and_a_new_file_is_reported_once_when_it_is_written()
    {
        await File.WriteAllTextAsync(Inbox("старый.pdf"), "был");
        await using var watcher = Create();
        watcher.Configure([Watch("*.pdf")]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        Assert.Empty(_events);

        await File.WriteAllTextAsync(Inbox("счёт.pdf"), "нач");
        await File.WriteAllTextAsync(Inbox("заметка.txt"), "не тот шаблон");
        await File.WriteAllTextAsync(Inbox("~$счёт.pdf"), "временный файл Office");
        await File.WriteAllTextAsync(Inbox(".скрытый.pdf"), "скрытый");
        await File.WriteAllTextAsync(Inbox("загрузка.pdf.crdownload"), "ещё скачивается");
        await watcher.ScanAsync(CancellationToken.None);
        // Файл ещё дописывается — события нет, пока он не перестанет меняться.
        _time.Advance(TimeSpan.FromSeconds(1));
        await File.AppendAllTextAsync(Inbox("счёт.pdf"), "ало и конец");
        await watcher.ScanAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await watcher.ScanAsync(CancellationToken.None);
        Assert.Empty(_events);
        _time.Advance(TimeSpan.FromSeconds(1.5));
        await watcher.ScanAsync(CancellationToken.None);
        await SettleAsync(watcher);

        var e = Assert.Single(_events);
        Assert.Equal(FileEvents.Source, e.Source);
        Assert.Equal(FileEvents.Created, e.Type);
        Assert.Equal("входящие", e.CorrelationKey);
        Assert.Equal("входящие/счёт.pdf", e.Payload!["path"]!.GetValue<string>());
        Assert.Equal("счёт.pdf", e.Payload["name"]!.GetValue<string>());
        Assert.Equal(".pdf", e.Payload["extension"]!.GetValue<string>());
        Assert.Equal(new FileInfo(Inbox("счёт.pdf")).Length, e.Payload["size"]!.GetValue<long>());
        Assert.StartsWith("w1:входящие/счёт.pdf:", e.ExternalId);
        var status = Assert.Single(watcher.Status);
        Assert.Null(status.Error);
        Assert.Equal(2, status.Files);
    }

    [Fact]
    public async Task Changed_file_is_an_event_only_when_asked_and_a_deleted_one_is_forgotten()
    {
        await File.WriteAllTextAsync(Inbox("отчёт.xlsx"), "1");
        await using var watcher = Create();
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        await File.WriteAllTextAsync(Inbox("отчёт.xlsx"), "22");
        await SettleAsync(watcher);
        Assert.Empty(_events);

        watcher.Configure([Watch(changes: true)]);
        await File.WriteAllTextAsync(Inbox("отчёт.xlsx"), "333");
        await SettleAsync(watcher);
        var changed = Assert.Single(_events);
        Assert.Equal(FileEvents.Changed, changed.Type);

        // Удалён и появился снова (с другим содержимым) — снова новый.
        File.Delete(Inbox("отчёт.xlsx"));
        await SettleAsync(watcher);
        await File.WriteAllTextAsync(Inbox("отчёт.xlsx"), "4444");
        await SettleAsync(watcher);
        Assert.Equal([FileEvents.Changed, FileEvents.Created], _events.Select(x => x.Type));
    }

    [Fact]
    public async Task Files_that_appeared_while_Pepa_was_closed_are_events_from_before_the_start()
    {
        await using (var first = Create())
        {
            first.Configure([Watch()]);
            first.Start(_time.GetUtcNow(), runLoop: false);
            await SettleAsync(first);
            await File.WriteAllTextAsync(Inbox("при-работе.txt"), "а");
            await SettleAsync(first);
        }

        Assert.Single(_events);
        // Pepa закрыта: появился файл.
        await File.WriteAllTextAsync(Inbox("без-pepa.txt"), "б");
        _time.Advance(TimeSpan.FromMinutes(10));

        await using var second = Create();
        second.Configure([Watch()]);
        Assert.True(second.WillCatchUp);
        var caughtUp = false;
        second.CaughtUp += () => caughtUp = true;
        var started = _time.GetUtcNow();
        second.Start(started, runLoop: false);
        await SettleAsync(second);

        Assert.True(caughtUp);
        Assert.Equal(2, _events.Count);
        var late = _events[1];
        Assert.Equal("входящие/без-pepa.txt", late.Payload!["path"]!.GetValue<string>());
        // Время — до запуска: ожидающие процессы его получат, а триггеры сами процессы не запустят.
        Assert.True(late.OccurredAt < started);

        // После досмотра новый файл — обычное событие.
        await File.WriteAllTextAsync(Inbox("после.txt"), "в");
        await SettleAsync(second);
        Assert.True(_events[2].OccurredAt >= started);
    }

    [Fact]
    public async Task Turning_watch_on_again_or_changing_its_pattern_starts_from_what_is_there()
    {
        await using var watcher = Create();
        watcher.Configure([Watch("*.pdf")]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        watcher.Configure([Watch("*.pdf") with { Enabled = false }]);
        await SettleAsync(watcher);
        await File.WriteAllTextAsync(Inbox("пока-выключено.pdf"), "x");
        await SettleAsync(watcher);
        watcher.Configure([Watch("*.pdf")]);
        await SettleAsync(watcher);

        await File.WriteAllTextAsync(Inbox("таблица.xlsx"), "y");
        await SettleAsync(watcher);
        watcher.Configure([Watch("*.pdf; *.xlsx")]);
        await SettleAsync(watcher);
        Assert.Empty(_events);

        await File.WriteAllTextAsync(Inbox("новая.xlsx"), "z");
        await SettleAsync(watcher);
        Assert.Equal("новая.xlsx", Assert.Single(_events).Payload!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Folder_that_disappeared_and_came_back_does_not_turn_its_files_into_events()
    {
        await using var watcher = Create();
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        Directory.Delete(Path.Combine(Workspace, "входящие"));
        await SettleAsync(watcher);
        Assert.Contains("нет в рабочей папке", Assert.Single(watcher.Status).Error);

        Directory.CreateDirectory(Path.Combine(Workspace, "входящие"));
        await File.WriteAllTextAsync(Inbox("вернулся.txt"), "x");
        await SettleAsync(watcher);
        Assert.Empty(_events);
        var status = Assert.Single(watcher.Status);
        Assert.Null(status.Error);
        Assert.Contains("была недоступна", status.Note);
    }

    [Fact]
    public async Task Event_that_could_not_be_passed_on_is_tried_again()
    {
        var attempts = 0;
        await using var watcher = Create(publish: input =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("база занята");
            }

            return Record(input);
        });
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        await File.WriteAllTextAsync(Inbox("a.txt"), "a");
        await SettleAsync(watcher);
        Assert.Empty(_events);
        Assert.Contains("база занята", Assert.Single(watcher.Status).Error);

        await watcher.ScanAsync(CancellationToken.None);
        Assert.Single(_events);
        Assert.Null(Assert.Single(watcher.Status).Error);
    }

    [Fact]
    public async Task Subfolders_are_watched_when_asked_and_too_many_files_is_an_error()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "входящие", "2026"));
        await using var watcher = Create(new FolderWatcherOptions { MaxFiles = 3 });
        watcher.Configure([Watch(subfolders: true)]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "входящие", "2026", "март.txt"), "x");
        await SettleAsync(watcher);
        Assert.Equal("входящие/2026/март.txt", Assert.Single(_events).Payload!["path"]!.GetValue<string>());

        foreach (var name in new[] { "1.txt", "2.txt", "3.txt" })
        {
            await File.WriteAllTextAsync(Inbox(name), name);
        }

        await SettleAsync(watcher);
        Assert.Contains("больше 3", Assert.Single(watcher.Status).Error);
        Assert.Single(_events);
    }

    [Theory]
    [InlineData("*.pdf", "Счёт.PDF", true)]
    [InlineData("*.pdf", "счёт.pdf.txt", false)]
    [InlineData("Z-7-*.pdf; *.xlsx", "Z-7-ответ.pdf", true)]
    [InlineData("Z-7-*.pdf; *.xlsx", "Z-8-ответ.pdf", false)]
    [InlineData("отчёт-??.txt", "отчёт-05.txt", true)]
    [InlineData("a+b(1).txt", "a+b(1).txt", true)]
    public void Name_pattern_matches_like_Windows(string pattern, string name, bool matches) =>
        Assert.Equal(matches, FileNamePattern.ToRegex(pattern).IsMatch(name));

    [Theory]
    [InlineData("", "Укажите")]
    [InlineData("папка/*.pdf", "без папок")]
    [InlineData(" ; ", "Укажите")]
    public void Bad_pattern_is_explained(string pattern, string reason) =>
        Assert.Contains(reason, FileNamePattern.Validate(pattern));

    [Fact]
    public async Task Process_waits_for_its_file_and_gets_its_path()
    {
        var watches = new List<FolderWatchSettings> { Watch() };
        string? root = null;
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterFileNodes(new Workspace(() => root!), () => watches));
        root = Path.Combine(h.Directory, "workspace");
        Directory.CreateDirectory(Path.Combine(root, "входящие"));
        await using var watcher = new FolderWatcher(new Workspace(root), Path.Combine(h.Directory, "watches"),
            (input, ct) => h.Engine.PublishEventAsync(input, ct), h.Time, NullLogger.Instance);
        watcher.Configure(watches);
        watcher.Start(h.Engine.StartedAt, runLoop: false);
        await watcher.ScanAsync(CancellationToken.None);

        var scenario = new ScenarioBuilder("Ждём ответ")
            .Variable("order", VariableType.Text, "Z-7")
            .Variable("file", VariableType.Any)
            .Node("start", "start")
            .Node("wait", WaitFileNode.TypeName, new { folder = "входящие", name = "{{ vars.order }}-*.pdf", saveTo = "file" })
            .Node("read", "file.read", new { path = "{{ vars.file.payload.path }}", saveTo = "order" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "read", Ports.Event).Chain("read", "end").Build();
        var id = await h.RunAsync(scenario);
        await h.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting);

        // Чужой файл процесс не будит.
        await File.WriteAllTextAsync(Path.Combine(root, "входящие", "Z-8-ответ.pdf"), "чужой");
        await File.WriteAllTextAsync(Path.Combine(root, "входящие", "Z-7-ответ.pdf"), "согласовано");
        await watcher.ScanAsync(CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(3));
        await watcher.ScanAsync(CancellationToken.None);

        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("согласовано", done.Variables["order"]!.GetValue<string>());
        Assert.Equal("Z-7-ответ.pdf", done.Variables["file"]!["payload"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Waiting_for_a_folder_Pepa_does_not_watch_stops_with_a_reason()
    {
        string? root = null;
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterFileNodes(new Workspace(() => root!), () => [Watch()]));
        root = Path.Combine(h.Directory, "workspace");
        var scenario = new ScenarioBuilder("Ждём")
            .Node("start", "start")
            .Node("wait", WaitFileNode.TypeName, new { folder = "загрузки" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "end", Ports.Event).Build();
        var id = await h.RunAsync(scenario);
        var stopped = await h.WaitForAsync(id, p => p.Status is ProcessStatus.NeedsAttention or ProcessStatus.Failed);
        Assert.Contains("не следит", stopped.StatusMessage);
    }

    [Fact]
    public async Task Empty_placeholder_next_to_a_download_is_not_reported_until_the_download_is_done()
    {
        await using var watcher = Create();
        watcher.Configure([Watch("*.pdf")]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        // Firefox: пустой «report.pdf» и данные в «report.pdf.part».
        await File.WriteAllTextAsync(Inbox("report.pdf"), "");
        await File.WriteAllTextAsync(Inbox("report.pdf.part"), "%PDF-1.7 часть");
        for (var i = 0; i < 15; i++)
        {
            await SettleAsync(watcher);
        }

        Assert.Empty(_events);
        // Скачивание закончилось: .part переименован поверх заготовки.
        File.Move(Inbox("report.pdf.part"), Inbox("report.pdf"), overwrite: true);
        await SettleAsync(watcher);
        await SettleAsync(watcher);
        var e = Assert.Single(_events);
        Assert.True(e.Payload!["size"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Empty_file_on_its_own_is_reported_after_a_longer_wait()
    {
        await using var watcher = Create();
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        await File.WriteAllTextAsync(Inbox("готово.flag"), "");
        await SettleAsync(watcher);
        Assert.Empty(_events);
        _time.Advance(TimeSpan.FromSeconds(20));
        await watcher.ScanAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(_events).Payload!["size"]!.GetValue<long>());
    }

    [Fact]
    public async Task Renamed_files_and_folders_are_not_new()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "входящие", "2026-10"));
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(Workspace, "входящие", "2026-10", $"{i}.pdf"), $"файл {i}");
        }

        await File.WriteAllTextAsync(Inbox("a.pdf"), "a");
        await using var watcher = Create();
        watcher.Configure([Watch(subfolders: true)]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        Directory.Move(Path.Combine(Workspace, "входящие", "2026-10"), Path.Combine(Workspace, "входящие", "октябрь"));
        File.Move(Inbox("a.pdf"), Inbox("a (обработан).pdf"));
        await SettleAsync(watcher);
        Assert.Empty(_events);

        await File.WriteAllTextAsync(Inbox("новый.pdf"), "новый");
        await SettleAsync(watcher);
        Assert.Equal("входящие/новый.pdf", Assert.Single(_events).Payload!["path"]!.GetValue<string>());
        Assert.Equal(7, Assert.Single(watcher.Status).Files);
    }

    [Fact]
    public async Task Folder_hidden_and_shown_again_does_not_turn_its_files_into_events()
    {
        var sub = Path.Combine(Workspace, "входящие", "сканы");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "1.pdf"), "1");
        await using var watcher = Create();
        watcher.Configure([Watch(subfolders: true)]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        var hidden = Path.Combine(Workspace, "входящие", ".сканы");
        Directory.Move(sub, hidden);
        await SettleAsync(watcher);
        Directory.Move(hidden, sub);
        await SettleAsync(watcher);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Damaged_list_of_known_files_is_explained_and_does_not_stop_other_watches()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "watches"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "watches", "watch-w1.json"), """{"key":"x","files":{"a":[null,1]}}""");
        await File.WriteAllTextAsync(Path.Combine(_dir, "watches", "watch-w2.json"), "{не json");
        Directory.CreateDirectory(Path.Combine(Workspace, "архив"));
        await using var watcher = Create();
        watcher.Configure([Watch(), Watch(folder: "архив") with { Id = "w2" }]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        Assert.Equal(2, watcher.Status.Count);
        Assert.All(watcher.Status, s => Assert.Contains("не прочитан", s.Note));
        Assert.All(watcher.Status, s => Assert.Null(s.Error));
        await File.WriteAllTextAsync(Inbox("после.txt"), "x");
        await SettleAsync(watcher);
        Assert.Single(_events);
    }

    [Fact]
    public async Task List_that_cannot_be_saved_keeps_its_error_until_it_is_saved()
    {
        // Вместо папки для списков — файл: сохранить не получится.
        await File.WriteAllTextAsync(Path.Combine(_dir, "watches"), "не папка");
        await using var watcher = Create();
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        await SettleAsync(watcher);
        Assert.Contains("Не удалось сохранить", Assert.Single(watcher.Status).Error);

        File.Delete(Path.Combine(_dir, "watches"));
        await watcher.ScanAsync(CancellationToken.None);
        Assert.Null(Assert.Single(watcher.Status).Error);
        Assert.True(File.Exists(Path.Combine(_dir, "watches", "watch-w1.json")));
    }

    [Fact]
    public async Task Folder_with_too_many_entries_is_an_error_even_if_few_match()
    {
        for (var i = 0; i < 6; i++)
        {
            await File.WriteAllTextAsync(Inbox($"{i}.txt"), "x");
        }

        await using var watcher = Create(new FolderWatcherOptions { MaxEntries = 5 });
        watcher.Configure([Watch("*.pdf")]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        Assert.Contains("больше 5 файлов и папок", Assert.Single(watcher.Status).Error);
    }

    [Fact]
    public async Task Live_file_after_the_folder_came_back_is_not_dated_before_the_start()
    {
        await using (var first = Create())
        {
            first.Configure([Watch()]);
            first.Start(_time.GetUtcNow(), runLoop: false);
            await SettleAsync(first);
        }

        // Pepa запускается, а папки нет (сетевой диск ещё не подключён).
        Directory.Delete(Path.Combine(Workspace, "входящие"));
        await using var second = Create();
        second.Configure([Watch()]);
        var started = _time.GetUtcNow();
        second.Start(started, runLoop: false);
        await SettleAsync(second);
        Directory.CreateDirectory(Path.Combine(Workspace, "входящие"));
        await SettleAsync(second);
        _time.Advance(TimeSpan.FromMinutes(30));
        await File.WriteAllTextAsync(Inbox("потом.txt"), "x");
        await SettleAsync(second);

        Assert.True(Assert.Single(_events).OccurredAt >= started);
    }

    [Fact]
    public async Task File_moved_in_while_Pepa_was_closed_reaches_the_process_that_waits_for_it()
    {
        var watches = new List<FolderWatchSettings> { Watch() };
        string? root = null;
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterFileNodes(new Workspace(() => root!), () => watches));
        root = Path.Combine(h.Directory, "workspace");
        Directory.CreateDirectory(Path.Combine(root, "входящие"));
        var state = Path.Combine(h.Directory, "watches");
        await using (var before = new FolderWatcher(new Workspace(root), state, (input, ct) => h.Engine.PublishEventAsync(input, ct), h.Time,
                         NullLogger.Instance))
        {
            before.Configure(watches);
            before.Start(h.Engine.StartedAt, runLoop: false);
            await before.ScanAsync(CancellationToken.None);
            var scenario = new ScenarioBuilder("Ждём")
                .Node("start", "start").Node("wait", WaitFileNode.TypeName, new { folder = "входящие" }).Node("end", "end")
                .Chain("start", "wait").Edge("wait", "end", Ports.Event).Build();
            var id = await h.RunAsync(scenario);
            await h.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting);
            h.Time.Advance(TimeSpan.FromMinutes(1));
            await before.ScanAsync(CancellationToken.None);
        }

        // Pepa закрыта: файл перенесли в папку — у него старое время создания и изменения.
        var moved = Path.Combine(root, "входящие", "вчерашний.pdf");
        await File.WriteAllTextAsync(moved, "вчера");
        File.SetCreationTimeUtc(moved, EngineHarness.StartTime.UtcDateTime.AddDays(-1));
        File.SetLastWriteTimeUtc(moved, EngineHarness.StartTime.UtcDateTime.AddDays(-1));
        h.Time.Advance(TimeSpan.FromMinutes(10));

        await using var after = new FolderWatcher(new Workspace(root), state, (input, ct) => h.Engine.PublishEventAsync(input, ct), h.Time,
            NullLogger.Instance);
        after.Configure(watches);
        after.Start(h.Time.GetUtcNow(), runLoop: false);
        await after.ScanAsync(CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(3));
        await after.ScanAsync(CancellationToken.None);

        var processes = await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 10 });
        await h.WaitForStatusAsync(processes.Single().Id, ProcessStatus.Completed);
    }

    [Theory]
    [InlineData("Ромашка; ООО")]
    [InlineData("*")]
    public async Task Name_from_a_variable_is_never_a_pattern(string client)
    {
        string? root = null;
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterFileNodes(new Workspace(() => root!), () => [Watch()]));
        root = Path.Combine(h.Directory, "workspace");
        Directory.CreateDirectory(Path.Combine(root, "входящие"));
        var scenario = new ScenarioBuilder("Ждём")
            .Variable("client", VariableType.Text, JsonValue.Create(client))
            .Node("start", "start")
            .Node("wait", WaitFileNode.TypeName, new { folder = "входящие", name = "{{ vars.client }}*.pdf" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "end", Ports.Event).Build();
        var id = await h.RunAsync(scenario);
        var stopped = await h.WaitForAsync(id, p => p.Status is ProcessStatus.NeedsAttention or ProcessStatus.Failed);
        Assert.Contains("из переменной есть", stopped.StatusMessage);
    }

    [Fact]
    public void Comma_is_part_of_a_name_not_a_separator()
    {
        Assert.Single(FileNamePattern.Split("Ромашка, ООО*.pdf"));
        Assert.Matches(FileNamePattern.ToRegex("Ромашка, ООО*.pdf"), "Ромашка, ООО — счёт.pdf");
        Assert.DoesNotMatch(FileNamePattern.ToRegex("Ромашка, ООО*.pdf"), "ООО Лютик.pdf");
    }

    [Fact]
    public async Task Files_of_a_hidden_folder_are_not_events_even_when_the_folder_is_shown()
    {
        // Скрыта ещё до начала слежения (на Linux скрытая — с точки в начале; «показать» — переименовать).
        var hidden = Path.Combine(Workspace, "входящие", ".архив");
        Directory.CreateDirectory(hidden);
        await File.WriteAllTextAsync(Path.Combine(hidden, "старый.pdf"), "старый");
        await using var watcher = Create();
        watcher.Configure([Watch(subfolders: true)]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        // Новый файл в скрытой папке — тоже не событие: скрытые папки Pepa не отслеживает.
        await File.WriteAllTextAsync(Path.Combine(hidden, "ещё.pdf"), "ещё");
        await SettleAsync(watcher);
        Directory.Move(hidden, Path.Combine(Workspace, "входящие", "архив"));
        await SettleAsync(watcher);
        Assert.Empty(_events);

        await File.WriteAllTextAsync(Path.Combine(Workspace, "входящие", "архив", "новый.pdf"), "новый");
        await SettleAsync(watcher);
        Assert.Equal("входящие/архив/новый.pdf", Assert.Single(_events).Payload!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task File_that_appeared_while_closed_and_was_renamed_before_it_settled_stays_from_before_the_start()
    {
        await using (var first = Create())
        {
            first.Configure([Watch()]);
            first.Start(_time.GetUtcNow(), runLoop: false);
            await SettleAsync(first);
        }

        await File.WriteAllTextAsync(Inbox("скачан.pdf"), "x");
        _time.Advance(TimeSpan.FromMinutes(5));
        await using var second = Create();
        second.Configure([Watch()]);
        var started = _time.GetUtcNow();
        second.Start(started, runLoop: false);
        await second.ScanAsync(CancellationToken.None);
        File.Move(Inbox("скачан.pdf"), Inbox("переименован.pdf"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await SettleAsync(second);

        var e = Assert.Single(_events);
        Assert.Equal("входящие/переименован.pdf", e.Payload!["path"]!.GetValue<string>());
        Assert.True(e.OccurredAt < started);
    }

    [Fact]
    public async Task Rename_in_the_scan_where_passing_an_event_failed_is_still_a_rename()
    {
        var fail = true;
        await using var watcher = Create(publish: input => fail ? throw new InvalidOperationException("база занята") : Record(input));
        await File.WriteAllTextAsync(Inbox("был.txt"), "был");
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        await File.WriteAllTextAsync(Inbox("a.txt"), "новый");
        await watcher.ScanAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(3));
        File.Move(Inbox("был.txt"), Inbox("z-переименован.txt"));
        await watcher.ScanAsync(CancellationToken.None);
        fail = false;
        await SettleAsync(watcher);

        Assert.Equal(["входящие/a.txt"], _events.Select(e => e.Payload!["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Renamed_away_and_replaced_file_is_not_new()
    {
        await File.WriteAllTextAsync(Inbox("журнал.txt"), "старое содержимое");
        await using var watcher = Create();
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);

        // Резервная копия при сохранении: старый файл — в .bak, на его месте — новый.
        File.Move(Inbox("журнал.txt"), Inbox("журнал.txt.bak"));
        await File.WriteAllTextAsync(Inbox("журнал.txt"), "новое содержимое, длиннее");
        await SettleAsync(watcher);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Path_that_became_a_link_and_back_does_not_turn_files_into_events()
    {
        await using (var first = Create())
        {
            first.Configure([Watch()]);
            first.Start(_time.GetUtcNow(), runLoop: false);
            await SettleAsync(first);
        }

        // Пока Pepa закрыта, на месте папки — ссылка (через ссылки Pepa не ходит), потом снова папка с файлами.
        var inbox = Path.Combine(Workspace, "входящие");
        Directory.Delete(inbox);
        Directory.CreateDirectory(Path.Combine(_dir, "другая"));
        Directory.CreateSymbolicLink(inbox, Path.Combine(_dir, "другая"));
        await using var second = Create();
        second.Configure([Watch()]);
        second.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(second);
        Assert.NotNull(Assert.Single(second.Status).Error);

        Directory.Delete(inbox);
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Inbox("был-там.txt"), "x");
        await SettleAsync(second);
        Assert.Empty(_events);
        Assert.Contains("была недоступна", Assert.Single(second.Status).Note);
    }

    [Fact]
    public async Task List_written_by_the_first_version_is_still_read()
    {
        await using (var first = Create())
        {
            first.Configure([Watch()]);
            first.Start(_time.GetUtcNow(), runLoop: false);
            await SettleAsync(first);
        }

        File.Move(Path.Combine(_dir, "watches", "watch-w1.json"), Path.Combine(_dir, "watches", "w1.json"));
        await File.WriteAllTextAsync(Inbox("без-pepa.txt"), "x");
        await using var second = Create();
        second.Configure([Watch()]);
        Assert.True(second.WillCatchUp);
        second.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(second);
        Assert.Equal("входящие/без-pepa.txt", Assert.Single(_events).Payload!["path"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_dir, "watches", "w1.json")));
    }

    [Fact]
    public async Task Known_files_are_saved_right_after_an_event()
    {
        await using var watcher = Create();
        watcher.Configure([Watch()]);
        watcher.Start(_time.GetUtcNow(), runLoop: false);
        await SettleAsync(watcher);
        await File.WriteAllTextAsync(Inbox("сразу.txt"), "x");
        await SettleAsync(watcher);

        Assert.Single(_events);
        // Без закрытия Pepa (сбой) — список уже на диске.
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "watches", "watch-w1.json")))!;
        Assert.NotNull(saved["files"]!["входящие/сразу.txt"]);
    }

    [Fact]
    public async Task Empty_value_from_a_variable_does_not_turn_the_name_into_a_pattern()
    {
        string? root = null;
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.RegisterFileNodes(new Workspace(() => root!), () => [Watch()]));
        root = Path.Combine(h.Directory, "workspace");
        Directory.CreateDirectory(Path.Combine(root, "входящие"));
        var scenario = new ScenarioBuilder("Ждём")
            .Variable("client", VariableType.Text, JsonValue.Create(" "))
            .Node("start", "start")
            .Node("wait", WaitFileNode.TypeName, new { folder = "входящие", name = "{{ vars.client }}*.pdf" })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "end", Ports.Event).Build();
        var id = await h.RunAsync(scenario);
        var stopped = await h.WaitForAsync(id, p => p.Status is ProcessStatus.NeedsAttention or ProcessStatus.Failed);
        Assert.Contains("получилась пустой", stopped.StatusMessage);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
