using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Smena.Core.Telegram;

namespace Smena.Core.Tests.Telegram;

public sealed class MiniAppInitDataTests
{
    private const string BotToken = "123456:TEST-token-for-unit-tests";

    private static readonly byte[] Key = MiniAppInitData.DeriveSecretKey(BotToken);
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    [Fact]
    public void Secret_key_matches_the_documented_derivation()
    {
        // Пример посчитан независимо: HMAC-SHA256(key = "WebAppData", message = токен).
        using var hmac = new HMACSHA256(Encoding.ASCII.GetBytes("WebAppData"));

        Assert.Equal(hmac.ComputeHash(Encoding.ASCII.GetBytes(BotToken)), Key);
    }

    [Fact]
    public void Genuine_data_gives_the_user()
    {
        var initData = Sign(Fields(userId: 777, username: "ivan_admin"));

        Assert.True(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out var user));
        Assert.Equal(new MiniAppUser(777, "ivan_admin"), user);
    }

    [Fact]
    public void User_without_username_is_accepted()
    {
        var initData = Sign(Fields(userId: 777, username: null));

        Assert.True(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out var user));
        Assert.Equal(new MiniAppUser(777, null), user);
    }

    [Fact]
    public void Order_of_fields_in_the_string_does_not_matter()
    {
        var fields = Fields(userId: 777, username: "ivan");
        var hash = Hash(fields, BotToken);

        // hash в начале, остальные поля в обратном порядке.
        var initData = "hash=" + hash + "&" + string.Join('&', fields.Reverse().Select(Encode));

        Assert.True(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out _));
    }

    [Fact]
    public void Cyrillic_name_inside_user_is_signed_correctly()
    {
        var fields = Fields(userId: 777, username: "ivan");
        fields["user"] = """{"id":777,"first_name":"Иван","last_name":"Петров & Ко","username":"ivan"}""";

        Assert.True(MiniAppInitData.TryValidate(Sign(fields), Key, Now, MaxAge, out var user));
        Assert.Equal(777, user.Id);
    }

    [Fact]
    public void Data_signed_by_another_bot_is_rejected()
    {
        var fields = Fields(userId: 777, username: "ivan");
        var initData = string.Join('&', fields.Select(Encode)) + "&hash=" + Hash(fields, "999999:other-bot-token");

        Assert.False(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out _));
    }

    [Fact]
    public void Changed_user_id_breaks_the_signature()
    {
        var initData = Sign(Fields(userId: 777, username: "ivan"));

        // Подменяем идентификатор, подпись оставляем прежней.
        var forged = initData.Replace("%22id%22%3A777", "%22id%22%3A778", StringComparison.Ordinal);

        Assert.NotEqual(initData, forged);
        Assert.False(MiniAppInitData.TryValidate(forged, Key, Now, MaxAge, out _));
    }

    [Fact]
    public void Extra_field_breaks_the_signature()
    {
        var initData = Sign(Fields(userId: 777, username: "ivan")) + "&is_admin=1";

        Assert.False(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(23 * 60, true)]
    [InlineData(24 * 60, true)]
    [InlineData((24 * 60) + 1, false)]
    [InlineData(-4, true)]   // часы сервера немного отстают
    [InlineData(-6, false)]  // «из будущего» — не верим
    public void Data_is_valid_only_for_a_limited_time(int minutesAgo, bool expected)
    {
        var initData = Sign(Fields(userId: 777, username: "ivan", authDate: Now.AddMinutes(-minutesAgo)));

        Assert.Equal(expected, MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hash=")]
    [InlineData("user=%7B%22id%22%3A777%7D&auth_date=1")]            // нет подписи
    [InlineData("user=%7B%22id%22%3A777%7D&hash=zz")]                 // подпись не hex
    [InlineData("=x&hash=00")]
    [InlineData("просто текст")]
    public void Garbage_is_rejected(string? initData)
    {
        Assert.False(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out _));
    }

    [Theory]
    [InlineData("""{"first_name":"Иван"}""")]   // нет id
    [InlineData("""{"id":"777"}""")]            // id строкой
    [InlineData("""{"id":0}""")]
    [InlineData("""{"id":-5}""")]
    [InlineData("""[777]""")]
    [InlineData("не json")]
    public void Correctly_signed_data_without_a_usable_user_is_rejected(string userJson)
    {
        var fields = Fields(userId: 1, username: null);
        fields["user"] = userJson;

        Assert.False(MiniAppInitData.TryValidate(Sign(fields), Key, Now, MaxAge, out _));
    }

    [Fact]
    public void Signed_data_without_user_or_date_is_rejected()
    {
        var withoutUser = Fields(userId: 777, username: null);
        withoutUser.Remove("user");
        var withoutDate = Fields(userId: 777, username: null);
        withoutDate.Remove("auth_date");

        Assert.False(MiniAppInitData.TryValidate(Sign(withoutUser), Key, Now, MaxAge, out _));
        Assert.False(MiniAppInitData.TryValidate(Sign(withoutDate), Key, Now, MaxAge, out _));
    }

    [Fact]
    public void Oversized_string_is_rejected_without_parsing()
    {
        var initData = Sign(Fields(userId: 777, username: new string('a', MiniAppInitData.MaxLength)));

        Assert.False(MiniAppInitData.TryValidate(initData, Key, Now, MaxAge, out _));
    }

    private static SortedDictionary<string, string> Fields(long userId, string? username, DateTimeOffset? authDate = null)
    {
        var user = username is null
            ? $$"""{"id":{{userId}},"first_name":"Test"}"""
            : $$"""{"id":{{userId}},"first_name":"Test","username":"{{username}}"}""";

        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = (authDate ?? Now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["query_id"] = "AAHdF6IQAAAAAN0XohDhrOrc",
            ["user"] = user,
        };
    }

    /// <summary>Собирает строку так же, как это делает Telegram.</summary>
    private static string Sign(SortedDictionary<string, string> fields) =>
        string.Join('&', fields.Select(Encode)) + "&hash=" + Hash(fields, BotToken);

    private static string Encode(KeyValuePair<string, string> field) => field.Key + "=" + WebUtility.UrlEncode(field.Value);

    private static string Hash(SortedDictionary<string, string> fields, string botToken)
    {
        var check = string.Join('\n', fields.Select(static field => field.Key + "=" + field.Value));
        var secret = HMACSHA256.HashData(Encoding.ASCII.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(check)));
    }
}
