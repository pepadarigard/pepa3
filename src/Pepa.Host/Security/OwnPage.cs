namespace Pepa.Host.Security;

/// <summary>
/// Страница — интерфейс Pepa (окно WebView2 пускает только его, мост к Windows отвечает только ему). Сравниваются схема,
/// узел и порт целиком: сравнение по началу строки пропустило бы «http://127.0.0.1:51870/…» (другой локальный сервер, чей
/// порт начинается с цифр порта Pepa) и «http://127.0.0.1:5187@evil.example/».
/// </summary>
public static class OwnPage
{
    public static bool Is(Uri baseUrl, string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return false;
        }

        // blob: — файлы, которые создаёт сама страница (экспорт сценария): blob:http://127.0.0.1:5187/<id>.
        var inner = uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ? uri["blob:".Length..] : uri;
        return Uri.TryCreate(inner, UriKind.Absolute, out var page) &&
               string.IsNullOrEmpty(page.UserInfo) &&
               string.Equals(page.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(page.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) &&
               page.Port == baseUrl.Port;
    }

    /// <summary>Сетевой путь (\\сервер\папка): даже проверка, есть ли такая папка, отправила бы серверу учётные данные Windows.</summary>
    public static bool IsNetworkPath(string? path) =>
        path is { Length: >= 2 } && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));
}
