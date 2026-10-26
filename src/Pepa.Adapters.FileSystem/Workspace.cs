using Pepa.Core.Nodes;

namespace Pepa.Adapters.FileSystem;

/// <summary>
/// Рабочая папка — единственное место, где файловым действиям разрешено что-либо читать и менять.
/// Любой путь разрешается относительно неё; выход за её пределы запрещён.
/// </summary>
public interface IWorkspace
{
    string Root { get; }

    /// <summary>Полный путь внутри рабочей папки или <see cref="NodeInputException"/> с понятной причиной.</summary>
    string Resolve(string? path, bool allowRoot = false);

    /// <summary>Путь относительно рабочей папки с разделителем «/».</summary>
    string ToRelative(string fullPath);

    void EnsureRootExists();
}

public sealed class Workspace(Func<string> rootProvider) : IWorkspace
{
    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Workspace(string root) : this(() => root)
    {
    }

    public string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootProvider()));

    public void EnsureRootExists() => Directory.CreateDirectory(Root);

    public string Resolve(string? path, bool allowRoot = false)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(path))
        {
            return allowRoot ? root : throw new NodeInputException("Путь не указан.");
        }

        var trimmed = path.Trim();
        var isAbsolute = Path.IsPathFullyQualified(trimmed);
        var checkedPart = isAbsolute && OperatingSystem.IsWindows() && trimmed.Length >= 2 && trimmed[1] == ':'
            ? trimmed[2..]
            : trimmed;
        if (checkedPart.IndexOfAny(ForbiddenChars) >= 0 || checkedPart.Any(char.IsControl))
        {
            throw new NodeInputException($"Путь «{path}» содержит недопустимые символы (< > : \" | ? *).");
        }

        var normalized = trimmed.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var full = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(isAbsolute ? normalized : Path.Combine(root, normalized)));

        var inside = full.Equals(root, Comparison) ||
                     full.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
        if (!inside)
        {
            throw new NodeInputException(
                $"Путь «{path}» ведёт за пределы рабочей папки. Разрешены только пути внутри «{root}».");
        }

        if (full.Equals(root, Comparison) && !allowRoot)
        {
            throw new NodeInputException("Укажите файл или папку внутри рабочей папки, а не саму рабочую папку.");
        }

        var relative = Path.GetRelativePath(root, full);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                throw new NodeInputException($"Имя «{segment}» зарезервировано Windows и не может использоваться.");
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new NodeInputException($"Имя «{segment}» не может заканчиваться точкой или пробелом.");
            }

            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                throw new NodeInputException(
                    $"Путь «{path}» проходит через символическую ссылку или junction — это запрещено, " +
                    "чтобы действие не вышло за пределы рабочей папки.");
            }
        }

        return full;
    }

    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // Битая ссылка тоже ссылка.
                var info = new FileInfo(path);
                return info.LinkTarget is not null;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
