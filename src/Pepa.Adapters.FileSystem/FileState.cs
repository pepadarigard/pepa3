using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pepa.Core;

namespace Pepa.Adapters.FileSystem;

/// <summary>Снимки состояния файлов — основа проверки «выполнилось ли действие».</summary>
internal static class FileState
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static JsonObject Snapshot(string path)
    {
        if (Directory.Exists(path))
        {
            return new JsonObject { ["exists"] = true, ["directory"] = true };
        }

        if (!File.Exists(path))
        {
            return new JsonObject { ["exists"] = false };
        }

        var info = new FileInfo(path);
        return new JsonObject
        {
            ["exists"] = true,
            ["directory"] = false,
            ["size"] = info.Length,
            ["sha256"] = Sha256(path),
        };
    }

    public static bool Exists(JsonObject? snapshot) => snapshot?.GetBoolean("exists") == true;

    public static string? Hash(JsonObject? snapshot) => snapshot?.GetString("sha256");

    public static long Size(JsonObject? snapshot) => (long)(snapshot?.GetNumber("size") ?? 0);

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>SHA-256 первых <paramref name="length"/> байт файла.</summary>
    public static string Sha256Prefix(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static byte[] ReadTail(string path, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < length)
        {
            return [];
        }

        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Атомарная запись: во временный файл рядом, затем переименование.</summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> data)
    {
        var temp = TempPathFor(path);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void CopyAtomically(string source, string destination)
    {
        var temp = TempPathFor(destination);
        try
        {
            File.Copy(source, temp, overwrite: false);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public const string TempMarker = ".pepa-tmp-";

    private static string TempPathFor(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{TempMarker}{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный файл останется; он помечен и не попадает в списки файлов.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
