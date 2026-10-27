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
