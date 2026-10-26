using System.Security.Cryptography;
using Pepa.Core.Security;
using Pepa.Engine.Persistence;

namespace Pepa.Host.Settings;

/// <summary>
/// Ключ API Anthropic для ИИ-помощника. Хранится в базе Pepa зашифрованным для пользователя Windows (DPAPI) и наружу
/// (в интерфейс, журналы) не отдаётся — только последние символы, чтобы узнать, какой ключ задан.
/// </summary>
public sealed class AssistantKeyStore(IPepaStore store)
{
    private const string SettingKey = "assistant.apiKey";
    private const string Purpose = "Pepa.Assistant";

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        if (!settings.TryGetValue(SettingKey, out var stored) || stored.Length == 0)
        {
            return null;
        }

        try
        {
            return Secrets.Unprotect(stored, Purpose);
        }
        catch (CryptographicException)
        {
            // Ключ зашифрован под другой учётной записью или на другом компьютере — как будто ключа нет.
            return null;
        }
    }

    public async Task SetAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var key = apiKey.Trim();
        if (key.Length < 20 || key.Length > 400 || key.Any(char.IsWhiteSpace))
        {
            throw new SettingsValidationException("Это не похоже на ключ API Anthropic (он выглядит как sk-ant-… без пробелов). Скопируйте его целиком.");
        }

        await using var write = await store.BeginWriteAsync(cancellationToken);
        await write.SetSettingAsync(SettingKey, Secrets.Protect(key, Purpose));
        await write.CommitAsync();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var write = await store.BeginWriteAsync(cancellationToken);
        await write.SetSettingAsync(SettingKey, "");
        await write.CommitAsync();
    }

    /// <summary>Как показать заданный ключ: последние 4 символа.</summary>
    public static string Hint(string key) => key.Length > 8 ? $"…{key[^4..]}" : "…";
}
