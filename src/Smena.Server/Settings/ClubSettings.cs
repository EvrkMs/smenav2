using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Shifts;
using Smena.Server.Notifications;

namespace Smena.Server.Settings;

/// <summary>
/// Настройки клуба, которые root меняет в панели. В таблице всегда ровно одна
/// строка: колонки типизированы и защищены ограничениями базы, в отличие от
/// таблицы «ключ — значение», где всё хранится строками.
/// </summary>
public sealed class ClubSettings
{
    public const short SingletonId = 1;

    public short Id { get; private set; } = SingletonId;

    public int CashFloat { get; set; } = 1000;

    public int MaxEmployeesPerShift { get; set; } = 3;

    public int MaxHoursPerShift { get; set; } = 12;

    public int MinShiftPhotos { get; set; } = 3;

    public int PhotoMaxAgeHours { get; set; } = 5;

    /// <summary>
    /// Часовой пояс клуба в формате IANA. Всё время хранится и обрабатывается в UTC;
    /// пояс нужен только там, где сервер сам обязан знать местное время: текст
    /// сообщений в Telegram, время съёмки фото без пояса, границы суток в отчётах.
    /// </summary>
    public string TimeZoneId { get; set; } = "Europe/Moscow";

    // ---- Telegram. Всё необязательно: не задан чат — сообщения этого вида не создаются. ----

    /// <summary>Чат администраторов: отчёты, сейф, расходы (у каждого своя тема).</summary>
    public long? TelegramChatId { get; set; }

    public int? TelegramReportsThreadId { get; set; }

    public int? TelegramSafeThreadId { get; set; }

    public int? TelegramExpensesThreadId { get; set; }

    /// <summary>Зарплатный чат: у каждого сотрудника в нём своя тема (employees.salary_thread_id).</summary>
    public long? TelegramSalaryChatId { get; set; }

    public bool HasTelegram => TelegramChatId is not null || TelegramSalaryChatId is not null;

    public TelegramRoute? ReportsRoute => Route(TelegramReportsThreadId);

    public TelegramRoute? SafeRoute => Route(TelegramSafeThreadId);

    public TelegramRoute? ExpensesRoute => Route(TelegramExpensesThreadId);

    // Тема обязательна: без неё три потока сообщений смешались бы в одной ленте.
    private TelegramRoute? Route(int? threadId) =>
        TelegramChatId is { } chatId && threadId is { } thread ? new TelegramRoute(chatId, thread) : null;

    /// <exception cref="TimeZoneNotFoundException">Пояс из базы неизвестен серверу.</exception>
    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public ShiftRules ToShiftRules() => new(
        cashFloat: CashFloat,
        maxEmployees: MaxEmployeesPerShift,
        maxTotalHours: MaxHoursPerShift,
        minPhotos: MinShiftPhotos,
        photoMaxAgeHours: PhotoMaxAgeHours);
}

internal sealed class ClubSettingsConfiguration : IEntityTypeConfiguration<ClubSettings>
{
    public void Configure(EntityTypeBuilder<ClubSettings> builder)
    {
        // Границы совпадают с проверками в ShiftRules: неверное значение
        // не попадёт в базу ни через сервер, ни при правке руками.
        builder.ToTable("club_settings", static table =>
        {
            table.HasCheckConstraint("ck_club_settings_singleton", "id = 1");
            table.HasCheckConstraint("ck_club_settings_cash_float", "cash_float >= 0");
            table.HasCheckConstraint("ck_club_settings_max_employees", "max_employees_per_shift >= 1");
            table.HasCheckConstraint("ck_club_settings_max_hours", "max_hours_per_shift >= 1");
            table.HasCheckConstraint("ck_club_settings_min_photos", "min_shift_photos >= 0");
            table.HasCheckConstraint("ck_club_settings_photo_age", "photo_max_age_hours >= 1");

            // Перевод времени в указанный пояс падает с ошибкой, если пояса не существует:
            // опечатку при ручной правке база не примет.
            table.HasCheckConstraint(
                "ck_club_settings_time_zone",
                "(timestamptz '2000-01-01 00:00:00+00' AT TIME ZONE time_zone_id) IS NOT NULL");
        });

        builder.HasKey(static settings => settings.Id);
        builder.Property(static settings => settings.Id).ValueGeneratedNever();
        builder.Property(static settings => settings.TimeZoneId).HasMaxLength(64);
        builder.Ignore(static settings => settings.HasTelegram);
        builder.Ignore(static settings => settings.ReportsRoute);
        builder.Ignore(static settings => settings.SafeRoute);
        builder.Ignore(static settings => settings.ExpensesRoute);

        builder.HasData(new ClubSettings());
    }
}
