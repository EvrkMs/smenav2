using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Smena.Client;

/// <summary>
/// Адрес сервера и токен устройства. Хранятся в профиле пользователя Windows;
/// токен зашифрован средствами Windows (DPAPI) и читается только под этой
/// учётной записью на этом компьютере.
/// </summary>
internal sealed record ConnectionSettings(Uri ServerUrl, string Token)
{
    public const int MinTokenLength = 32;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Smena.Client",
        "connection.json");

    /// <summary>Каталог данных программы: настройки подключения и профиль WebView2.</summary>
    public static string DataDirectory => Path.GetDirectoryName(FilePath)!;

    /// <summary>Адрес интерфейса кассы на сервере.</summary>
    public Uri CashierUrl => new(ServerUrl, "/cashier/");

    public static ConnectionSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath));
            if (stored is null || !TryParseServerUrl(stored.ServerUrl, out var url))
            {
                return null;
            }

            var token = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedToken), null, DataProtectionScope.CurrentUser));

            return token.Length >= MinTokenLength ? new ConnectionSettings(url, token) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or CryptographicException or UnauthorizedAccessException)
        {
            // Файл повреждён или создан под другой учётной записью — спросим настройки заново.
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);

        var protectedToken = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.CurrentUser));

        File.WriteAllText(FilePath, JsonSerializer.Serialize(new StoredSettings(ServerUrl.ToString(), protectedToken)));
    }

    public static bool TryParseServerUrl(string? text, out Uri url)
    {
        if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            // Только адрес сервера, без пути: интерфейс всегда лежит в /cashier/.
            url = new Uri(parsed.GetLeftPart(UriPartial.Authority));
            return true;
        }

        url = null!;
        return false;
    }

    private sealed record StoredSettings(string ServerUrl, string ProtectedToken);
}
