using System.Security.Cryptography;
using System.Text;

namespace Pepa.Host.Startup;

/// <summary>
/// Запись «Запускать вместе с Windows» (D-086) без реестра: имя значения в HKCU\…\Run, командная строка и её разбор. Сам
/// реестр трогает оконное приложение; здесь — то, что можно проверить на любой системе.
/// </summary>
public static class AutostartEntry
{
    public const string MinimizedArgument = "--minimized";
    public const string DataDirectoryArgument = "--data-dir";

    /// <summary>Папка данных без завершающей черты (корень диска — «D:\» — остаётся как есть): одна папка — одно имя.</summary>
    public static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Имя значения: у папки данных по умолчанию — «Pepa», у своей — с хешем полного пути. По одному имени папки
    /// (D:\Pepa\data и D:\PepaTest\data — обе «data») записи двух копий Pepa заменяли бы друг друга.
    /// </summary>
    public static string ValueName(string? dataDirectory) =>
        dataDirectory is null ? "Pepa" : $"Pepa ({Leaf(dataDirectory)}, {Hash(dataDirectory)})";

    /// <summary>Имя, под которым запись хранилась раньше (только по имени папки), — чтобы перенести её на новое.</summary>
    public static string LegacyValueName(string? dataDirectory) =>
        dataDirectory is null ? "Pepa" : $"Pepa ({Path.GetFileName(dataDirectory)})";

    public static string Hash(string dataDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeDirectory(dataDirectory).ToUpperInvariant())))[..16];

    private static string Leaf(string dataDirectory)
    {
        var normalized = NormalizeDirectory(dataDirectory);
        var leaf = Path.GetFileName(normalized);
        return leaf.Length > 0 ? leaf : normalized.TrimEnd('\\', '/', ':');
    }

    /// <summary>Командная строка автозапуска: свёрнуто в область уведомлений, своя папка данных — явно.</summary>
    public static string Command(string executable, string? dataDirectory) =>
        $"{Quote(executable)} {Arguments(dataDirectory)}";

    /// <summary>Аргументы без пути к программе (для перезапуска после обновления — RegisterApplicationRestart).</summary>
    public static string Arguments(string? dataDirectory) =>
        MinimizedArgument + (dataDirectory is null ? "" : $" {DataDirectoryArgument} {Quote(NormalizeDirectory(dataDirectory))}");

    /// <summary>
    /// Аргумент в кавычках по правилам Windows (CommandLineToArgvW): черты перед кавычкой удваиваются — иначе «D:\» в конце
    /// превратился бы в «D:"» и Pepa не нашла бы свою папку.
    /// </summary>
    public static string Quote(string argument)
    {
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                text.Append('\\', slashes * 2 + 1).Append('"');
            }
            else
            {
                text.Append('\\', slashes).Append(c);
            }

            slashes = 0;
        }

        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>Разобрать командную строку по правилам Windows (как её разберёт запущенная программа).</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        var i = 0;

        // Путь к программе — по своим правилам: до закрывающей кавычки или пробела, черты не особые.
        while (i < commandLine.Length && commandLine[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < commandLine.Length && commandLine[i] == '"')
        {
            var end = commandLine.IndexOf('"', i + 1);
            result.Add(end < 0 ? commandLine[(i + 1)..] : commandLine[(i + 1)..end]);
            i = end < 0 ? commandLine.Length : end + 1;
        }
        else
        {
            var start = i;
            while (i < commandLine.Length && commandLine[i] is not (' ' or '\t'))
            {
                i++;
            }

            if (i > start)
            {
                result.Add(commandLine[start..i]);
            }
        }

        for (; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                any = true;
            }
            else if (c == '"')
            {
                if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                any = true;
            }
            else if (c is ' ' or '\t' && !quoted)
            {
                if (any)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>Папка данных из аргументов (--data-dir), полная и без завершающей черты; null — по умолчанию.</summary>
    public static string? DataDirectoryFrom(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == DataDirectoryArgument)
            {
                return NormalizeDirectory(arguments[i + 1]);
            }
        }

        return null;
    }

    /// <summary>Запускает ли эта команда Pepa с этой папкой данных (null — по умолчанию).</summary>
    public static bool Starts(string command, string? dataDirectory)
    {
        var arguments = Split(command);
        var target = arguments.Count == 0 ? null : DataDirectoryFrom(arguments.Skip(1).ToList());
        return dataDirectory is null
            ? target is null
            : target is not null && string.Equals(target, NormalizeDirectory(dataDirectory), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Не выключила ли запись сама Windows (диспетчер задач → «Автозагрузка», «Параметры → Приложения → Автозагрузка»):
    /// HKCU\…\Explorer\StartupApproved\Run. Нет значения — включено; первый байт 02/06 — включено, 03/07 (нечётный) — выключено.
    /// </summary>
    public static bool IsApproved(byte[]? approval) => approval is not { Length: > 0 } || (approval[0] & 1) == 0;
}
