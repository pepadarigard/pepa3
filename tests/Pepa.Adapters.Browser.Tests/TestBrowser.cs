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

    /// <summary>
    /// PEPA_TEST_REQUIRE_BROWSER=1 (так в CI): без браузера тесты не пропускаются, а падают — иначе сборка выглядела бы
    /// зелёной, хотя блоки браузера и AdsPower не проверялись.
    /// </summary>
    private static bool Required => Environment.GetEnvironmentVariable("PEPA_TEST_REQUIRE_BROWSER") == "1";

    public static void RequireBrowser() =>
        SkipOrFail(Available, "Браузер для тестов не найден (задайте PEPA_TEST_BROWSER_PATH или PEPA_TEST_BROWSER_CHANNEL).");

    public static void RequireCdpBrowser() =>
        SkipOrFail(ExecutableForCdp is not null, "Исполняемый файл браузера для имитации AdsPower не найден (PEPA_TEST_BROWSER_PATH).");

    private static void SkipOrFail(bool available, string reason)
    {
        if (!available && Required)
        {
            Assert.Fail(reason + " В CI браузер обязателен (PEPA_TEST_REQUIRE_BROWSER=1).");
        }

        Assert.SkipUnless(available, reason);
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
