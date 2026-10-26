namespace Pepa.Adapters.Browser.Tests;

/// <summary>
/// Какой браузер использовать в тестах: PEPA_TEST_BROWSER_PATH (исполняемый файл) или PEPA_TEST_BROWSER_CHANNEL
/// (msedge/chrome); иначе — Chromium из /opt/pw-browsers, Edge на Windows. Нет браузера — тесты пропускаются.
/// </summary>
internal static class TestBrowser
{
    public static string? ExecutablePath { get; } = FindExecutable();

    public static string? Channel { get; } = Environment.GetEnvironmentVariable("PEPA_TEST_BROWSER_CHANNEL") is { Length: > 0 } channel
        ? channel
        : ExecutablePath is null && OperatingSystem.IsWindows() ? "msedge" : null;

    public static bool Available => ExecutablePath is not null || Channel is not null;

    /// <summary>Исполняемый файл для поддельного AdsPower (нужен путь, канала мало).</summary>
    public static string? ExecutableForCdp { get; } = ExecutablePath ?? FindInstalled();

    public static void RequireBrowser()
    {
        Assert.SkipUnless(Available, "Браузер для тестов не найден (задайте PEPA_TEST_BROWSER_PATH или PEPA_TEST_BROWSER_CHANNEL).");
    }

    public static void RequireCdpBrowser()
    {
        Assert.SkipUnless(ExecutableForCdp is not null, "Исполняемый файл браузера для имитации AdsPower не найден (PEPA_TEST_BROWSER_PATH).");
    }

    private static string? FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("PEPA_TEST_BROWSER_PATH") is { Length: > 0 } path && File.Exists(path))
        {
            return path;
        }

        if (Environment.GetEnvironmentVariable("PEPA_TEST_BROWSER_CHANNEL") is { Length: > 0 })
        {
            return null;
        }

        const string root = "/opt/pw-browsers";
        if (Directory.Exists(root))
        {
            return Directory.GetDirectories(root, "chromium-*")
                .Select(d => Path.Combine(d, "chrome-linux64", "chrome"))
                .FirstOrDefault(File.Exists);
        }

        return null;
    }

    private static string? FindInstalled()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ]
            : ["/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge"];
        return candidates.FirstOrDefault(File.Exists);
    }
}
