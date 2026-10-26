using System.Security.Cryptography;
using System.Text;

namespace Pepa.Adapters.Telegram;

/// <summary>
/// Секреты в файлах Pepa (api_hash, ключ сессии): в Windows зашифрованы для текущего пользователя (DPAPI) — скопированная
/// папка данных на другом компьютере или под другой учётной записью их не раскроет. Вне Windows хранятся как есть.
/// </summary>
internal static class Secrets
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "Pepa.Telegram"u8.ToArray();

    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return value;
        }

        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new CryptographicException("Данные зашифрованы в Windows и здесь недоступны.");
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
    }
}
