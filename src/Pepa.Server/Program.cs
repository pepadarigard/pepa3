using Pepa.Host;

// Pepa без окна: движок и интерфейс в обычном браузере.
// Пример: pepa-server --data-dir ./.data --port 5180
var options = ParseArguments(args);
if (options is null)
{
    Console.WriteLine("""
                      Pepa — сервер без окна.

                        --data-dir <папка>    папка данных (по умолчанию — как у оконного приложения)
                        --workspace <папка>   рабочая папка для файловых действий (если ещё не задана в настройках)
                        --port <число>        порт на 127.0.0.1 (по умолчанию любой свободный)
                        --token <строка>      постоянный токен доступа (по умолчанию случайный)
                        --ui <папка>          папка собранного интерфейса
                        --no-samples          не создавать примеры сценариев
                      """);
    return 1;
}

PepaHost host;
try
{
    host = await PepaHost.StartAsync(options);
}
catch (InstanceAlreadyRunningException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

Console.WriteLine($"Pepa запущена. Откройте в браузере:\n  {host.LaunchUrl}\nОстановка — Ctrl+C.");
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
Console.WriteLine("Остановка…");
await host.StopAsync();
return 0;

static PepaHostOptions? ParseArguments(string[] args)
{
    string? dataDirectory = null, workspace = null, token = null, ui = null;
    var port = 0;
    var samples = true;
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Нет значения для {args[i]}");
        switch (args[i])
        {
            case "--data-dir": dataDirectory = Next(); break;
            case "--workspace": workspace = Next(); break;
            case "--port": port = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--token": token = Next(); break;
            case "--ui": ui = Next(); break;
            case "--no-samples": samples = false; break;
            default: return null;
        }
    }

    return new PepaHostOptions
    {
        DataDirectory = dataDirectory ?? HostPaths.DefaultDataDirectory(),
        DefaultWorkspaceRoot = workspace is null ? null : Path.GetFullPath(workspace),
        Port = port,
        Token = token,
        UiDirectory = ui is null ? null : Path.GetFullPath(ui),
        SeedSamples = samples,
        ConsoleLogging = true,
    };
}
