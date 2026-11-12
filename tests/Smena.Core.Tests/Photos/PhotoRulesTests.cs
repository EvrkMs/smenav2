using Smena.Core.Photos;

namespace Smena.Core.Tests.Photos;

public sealed class PhotoRulesTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private static readonly DateTimeOffset Uploaded = new(2026, 11, 11, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Time_without_offset_is_club_time()
    {
        var taken = PhotoRules.ParseExifTime("2026:11:11 08:30:15", offset: null, Moscow);

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 5, 30, 15, TimeSpan.Zero), taken);
    }

    [Theory]
    [InlineData("+05:00", 3)]
    [InlineData("-02:00", 10)]
    [InlineData("+00:00", 8)]
    public void Offset_from_the_file_wins_over_club_zone(string offset, int expectedUtcHour)
    {
        var taken = PhotoRules.ParseExifTime("2026:11:11 08:30:15", offset, Moscow);

        Assert.Equal(new DateTimeOffset(2026, 11, 11, expectedUtcHour, 30, 15, TimeSpan.Zero), taken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+3")]
    [InlineData("03:00")]
    [InlineData("+25:00")]
    [InlineData("мусор!")]
    public void Unreadable_offset_falls_back_to_club_zone(string offset)
    {
        var taken = PhotoRules.ParseExifTime("2026:11:11 08:30:15", offset, Moscow);

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 5, 30, 15, TimeSpan.Zero), taken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("                   ")]
    [InlineData("2026-11-11T08:30:15")]
    public void Missing_or_broken_time_is_null(string? dateTime)
    {
        Assert.Null(PhotoRules.ParseExifTime(dateTime, offset: null, Moscow));
    }

    [Fact]
    public void Trailing_zero_bytes_are_ignored()
    {
        // Некоторые камеры дописывают к строке нулевой байт.
        var taken = PhotoRules.ParseExifTime("2026:11:11 08:30:15\0", "+03:00\0", Moscow);

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 5, 30, 15, TimeSpan.Zero), taken);
    }

    [Fact]
    public void Photo_without_time_is_stale()
    {
        Assert.True(PhotoRules.IsStale(takenAt: null, Uploaded, maxAgeHours: 5));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(299, false)]
    [InlineData(300, false)] // ровно 5 часов — ещё годится
    [InlineData(301, true)]
    [InlineData(60 * 24, true)]
    public void Photo_older_than_the_limit_is_stale(int minutesBeforeUpload, bool expected)
    {
        var taken = Uploaded.AddMinutes(-minutesBeforeUpload);

        Assert.Equal(expected, PhotoRules.IsStale(taken, Uploaded, maxAgeHours: 5));
    }

    [Fact]
    public void Photo_from_a_phone_with_fast_clock_is_not_stale()
    {
        // Часы телефона спешат: снимок «из будущего» старым не считается.
        Assert.False(PhotoRules.IsStale(Uploaded.AddMinutes(10), Uploaded, maxAgeHours: 5));
    }
}
