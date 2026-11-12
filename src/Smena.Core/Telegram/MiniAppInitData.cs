using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Smena.Core.Telegram;

/// <summary>Пользователь Telegram, от имени которого открыт MiniApp.</summary>
public readonly record struct MiniAppUser(long Id, string? Username);

/// <summary>
/// Проверка строки <c>initData</c>, которую Telegram передаёт MiniApp при запуске.
/// Строка подписана токеном бота, поэтому подделать её, не зная токена, нельзя:
/// если подпись сходится, идентификатору пользователя внутри можно верить.
/// Алгоритм — из документации Telegram («Validating data received via the Mini App»).
/// </summary>
public static class MiniAppInitData
{
    /// <summary>Длиннее настоящая строка не бывает; всё, что больше, даже не разбираем.</summary>
    public const int MaxLength = 4096;

    private const int HashBytes = 32;

    /// <summary>
    /// Ключ подписи: HMAC-SHA256 от токена бота с ключом «WebAppData».
    /// Считается один раз при старте сервера.
    /// </summary>
    public static byte[] DeriveSecretKey(string botToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        return HMACSHA256.HashData("WebAppData"u8, Encoding.UTF8.GetBytes(botToken));
    }

    /// <param name="initData">Строка вида <c>query_id=…&amp;user=…&amp;auth_date=…&amp;hash=…</c>.</param>
    /// <param name="secretKey">Результат <see cref="DeriveSecretKey"/>.</param>
    /// <param name="now">Текущее время.</param>
    /// <param name="maxAge">Сколько строка считается действительной после выдачи.</param>
    /// <param name="user">Пользователь из строки, если она подлинная и не устарела.</param>
    public static bool TryValidate(
        string? initData,
        ReadOnlySpan<byte> secretKey,
        DateTimeOffset now,
        TimeSpan maxAge,
        out MiniAppUser user)
    {
        user = default;

        if (string.IsNullOrEmpty(initData) || initData.Length > MaxLength)
        {
            return false;
        }

        var pairs = new List<KeyValuePair<string, string>>(capacity: 8);
        string? hash = null;

        foreach (var part in initData.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var key = part[..separator];
            var value = WebUtility.UrlDecode(part[(separator + 1)..]);

            if (key == "hash")
            {
                hash = value;
            }
            else
            {
                pairs.Add(new(key, value));
            }
        }

        if (hash is null || hash.Length != HashBytes * 2)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[HashBytes];
        if (Convert.FromHexString(hash, expected, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != HashBytes)
        {
            return false;
        }

        // Подписывается строка «ключ=значение» по всем полям, кроме hash,
        // отсортированным по ключу и разделённым переводом строки.
        pairs.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        var check = new StringBuilder(initData.Length);
        foreach (var (key, value) in pairs)
        {
            if (check.Length > 0)
            {
                check.Append('\n');
            }

            check.Append(key).Append('=').Append(value);
        }

        Span<byte> actual = stackalloc byte[HashBytes];
        HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(check.ToString()), actual);

        // Сравнение за постоянное время: по времени ответа нельзя подбирать подпись побайтно.
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return false;
        }

        // Дальше данным можно верить; остаётся проверить срок и достать пользователя.
        string? authDate = null;
        string? userJson = null;
        foreach (var (key, value) in pairs)
        {
            if (key == "auth_date")
            {
                authDate = value;
            }
            else if (key == "user")
            {
                userJson = value;
            }
        }

        if (!long.TryParse(authDate, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var issuedSeconds))
        {
            return false;
        }

        // Перехваченная строка не должна работать вечно. Небольшой запас «в будущее» —
        // на расхождение часов сервера и Telegram.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(issuedSeconds);
        if (age > maxAge || age < -TimeSpan.FromMinutes(5))
        {
            return false;
        }

        return TryReadUser(userJson, out user);
    }

    private static bool TryReadUser(string? json, out MiniAppUser user)
    {
        user = default;

        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number // TryGetInt64 на строке бросает, а не возвращает false
                || !id.TryGetInt64(out var userId)
                || userId <= 0)
            {
                return false;
            }

            var username = root.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

            user = new MiniAppUser(userId, username);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
