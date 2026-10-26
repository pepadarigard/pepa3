using System.Security.Cryptography;
using System.Text;

namespace Pepa.Core.Security;

/// <summary>
/// Секреты Pepa (api_hash и ключ сессии Telegram, ключ ИИ-помощника): в Windows зашифрованы для текущего пользователя
/// (DPAPI) — скопированная папка данных на другом компьютере или под другой учётной записью их не раскроет. Вне Windows
/// хранятся как есть.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    /// <param name="purpose">Для чего секрет («Pepa.Telegram»): секрет одного назначения не раскрывается как другой.</param>
    public static string Protect(string value, string purpose)
    {
        if (!OperatingSystem.IsWindows())
        {
            return value;
        }

        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy(purpose), DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string stored, string purpose)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new CryptographicException("Данные зашифрованы в Windows и здесь недоступны.");
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy(purpose), DataProtectionScope.CurrentUser));
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes(purpose);
}
