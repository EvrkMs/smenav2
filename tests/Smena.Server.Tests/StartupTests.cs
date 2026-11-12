using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Smena.Server.Tests;

public sealed class StartupTests(SmenaApp app)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_ok(string path)
    {
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Root_address_leads_to_the_cashier_ui()
    {
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/cashier/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unknown_api_address_is_not_swallowed_by_the_ui_fallback()
    {
        using var client = app.CreateCashierClient();

        using var response = await client.GetAsync(new Uri("/api/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Migrations_are_applied_on_startup()
    {
        var pending = await app.QueryDatabaseAsync(db => db.Database.GetPendingMigrationsAsync());

        Assert.Empty(pending);
    }

    [Fact]
    public async Task Club_settings_are_seeded_with_agreed_defaults()
    {
        // Другие тесты меняют настройки под себя; сброс возвращает значения по умолчанию.
        await app.ResetLedgerAsync();

        var settings = await app.QueryDatabaseAsync(db => db.ClubSettings.AsNoTracking().SingleAsync());

        Assert.Equal(1000, settings.CashFloat);
        Assert.Equal(3, settings.MaxEmployeesPerShift);
        Assert.Equal(12, settings.MaxHoursPerShift);
        Assert.Equal(3, settings.MinShiftPhotos);
        Assert.Equal(5, settings.PhotoMaxAgeHours);

        // Настройки из базы обязаны проходить проверки ядра.
        var rules = settings.ToShiftRules();
        Assert.Equal(settings.CashFloat, rules.CashFloat);
    }

    [Fact]
    public async Task Database_rejects_a_second_settings_row()
    {
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
                "insert into club_settings (id, cash_float, max_employees_per_shift, max_hours_per_shift, min_shift_photos, photo_max_age_hours, time_zone_id) " +
                "values (2, 1000, 3, 12, 3, 5, 'Europe/Moscow')")));

        Assert.Equal("ck_club_settings_singleton", error.ConstraintName);
    }

    [Fact]
    public async Task Club_time_zone_defaults_to_moscow()
    {
        var settings = await app.QueryDatabaseAsync(db => db.ClubSettings.AsNoTracking().SingleAsync());

        var noonUtc = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var clubTime = TimeZoneInfo.ConvertTime(noonUtc, settings.GetTimeZone());

        Assert.Equal("Europe/Moscow", settings.TimeZoneId);
        Assert.Equal(15, clubTime.Hour);
        // Момент времени тот же — меняется только представление.
        Assert.Equal(noonUtc, clubTime);
    }

    [Fact]
    public async Task Database_rejects_unknown_time_zone()
    {
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
                "update club_settings set time_zone_id = 'Mars/Olympus'")));

        // 22023 — invalid_parameter_value: «time zone "Mars/Olympus" not recognized».
        Assert.Equal("22023", error.SqlState);
    }

    [Fact]
    public async Task Database_rejects_out_of_range_settings()
    {
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
                "update club_settings set max_employees_per_shift = 0")));

        Assert.Equal("ck_club_settings_max_employees", error.ConstraintName);
    }
}
