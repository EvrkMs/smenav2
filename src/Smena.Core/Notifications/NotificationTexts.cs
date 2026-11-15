using System.Globalization;
using System.Text;
using Smena.Core.Shifts;

namespace Smena.Core.Notifications;

/// <summary>Строка движения денег в сообщении: на сколько изменился счёт и почему.</summary>
public readonly record struct MoneyLine(int SignedAmount, string Label);

/// <summary>
/// Тексты сообщений для Telegram. Чистые функции: текст собирается в момент
/// операции и кладётся в outbox готовым, поэтому отправитель ничего не считает.
/// Первая строка каждого сообщения — время операции во времени клуба.
/// </summary>
public static class NotificationTexts
{
    /// <summary>Ответ на сообщение отменённой операции.</summary>
    public const string Cancelled = "Отмена";

    /// <summary>Отчёт о закрытии смены — в тему отчётов.</summary>
    /// <param name="input">Что ввёл администратор.</param>
    /// <param name="calculation">Что посчитала система.</param>
    /// <param name="closedAtLocal">Момент закрытия во времени клуба.</param>
    /// <param name="photoCount">Сколько фото приложено к смене.</param>
    /// <param name="pastedPhotoCount">Сколько из них вставлено на кассе из буфера обмена.</param>
    public static string ShiftReport(
        ShiftInput input,
        ShiftCalculation calculation,
        DateTimeOffset closedAtLocal,
        int photoCount = 0,
        int pastedPhotoCount = 0)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(calculation);

        var text = new StringBuilder(capacity: 512);
        AppendHeader(text, closedAtLocal);
        text.AppendLine();
        text.Append("Нал: ").AppendLine(Money(input.FactCash));
        text.Append("Б/Н: ").AppendLine(Money(input.FactNonCash));
        text.Append("Выручка: ").AppendLine(Money(calculation.Revenue));
        text.Append("Итог: ").AppendLine(Money(calculation.Total));
        text.AppendLine();
        text.Append("Расхождение по кассе: ").AppendLine(Signed(calculation.CashDiscrepancy));
        text.Append("Расхождение по сейфу: ").AppendLine(Signed(calculation.SafeDiscrepancy));
        text.AppendLine();
        text.Append("Факт сейфа: ").AppendLine(Money(input.FactSafe));
        text.AppendLine();
        text.AppendLine("== программные данные ==");
        text.Append("LanGame: нал ").Append(Money(input.LangameCash)).Append(", б/н ").AppendLine(Money(input.LangameNonCash));
        text.Append("МойСклад: нал ").Append(Money(input.SkladCash)).Append(", б/н ").AppendLine(Money(input.SkladNonCash));
        text.Append("Сейф: ").AppendLine(Money(calculation.ExpectedSafe));

        if (!string.IsNullOrWhiteSpace(input.ShortageReason))
        {
            text.AppendLine();
            text.Append("Причина недостачи: ").AppendLine(input.ShortageReason.Trim());
        }

        var lines = calculation.Lines;
        if (lines.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Сотрудники:");
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                text.Append("- ").Append(line.Name).Append(": ").Append(line.Hours).Append(" ч");
                if (line.Minus > 0)
                {
                    text.Append(", минус ").Append(Money(line.Minus));
                }

                text.Append(", ЗП ").AppendLine(Money(line.Payable));
            }

            text.AppendLine();
            text.Append("Всего ЗП: ").AppendLine(Money(calculation.PayrollNet));
        }

        // Давность снимков в отчёт не пишется: её проверяет root в панели.
        if (photoCount > 0)
        {
            text.AppendLine();
            text.Append("Фото: ").Append(photoCount);
            if (pastedPhotoCount > 0)
            {
                text.Append(" (с кассы: ").Append(pastedPhotoCount).Append(')');
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Движения по сейфу за одну операцию и остаток после неё — в тему сейфа.</summary>
    public static string SafeMovements(IReadOnlyList<MoneyLine> lines, int balanceAfter, DateTimeOffset occurredAtLocal) =>
        Movements(lines, "В сейфе: ", balanceAfter, occurredAtLocal);

    /// <summary>Движения по балансу сотрудника за одну операцию — в его личную тему.</summary>
    public static string EmployeeMovements(IReadOnlyList<MoneyLine> lines, int balanceAfter, DateTimeOffset occurredAtLocal) =>
        Movements(lines, "Баланс: ", balanceAfter, occurredAtLocal);

    /// <summary>Расход из сейфа — в тему расходов.</summary>
    public static string Expense(int amount, string category, string? comment, string actorName, DateTimeOffset occurredAtLocal)
    {
        var text = new StringBuilder(capacity: 128);
        AppendHeader(text, occurredAtLocal);
        text.Append("Расход ").Append(Money(amount)).Append(' ').AppendLine(category);
        if (!string.IsNullOrWhiteSpace(comment))
        {
            text.AppendLine(comment.Trim());
        }

        text.Append("Провёл: ").Append(actorName);
        return text.ToString();
    }

    private static string Movements(IReadOnlyList<MoneyLine> lines, string balanceLabel, int balanceAfter, DateTimeOffset occurredAtLocal)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var text = new StringBuilder(capacity: 64 + (lines.Count * 48));
        AppendHeader(text, occurredAtLocal);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            text.Append(Signed(line.SignedAmount));
            if (line.Label.Length > 0)
            {
                text.Append(' ').Append(line.Label);
            }

            text.AppendLine();
        }

        text.Append(balanceLabel).Append(Money(balanceAfter));
        return text.ToString();
    }

    /// <summary>Время операции: 2026.11.10 21:30.</summary>
    private static void AppendHeader(StringBuilder text, DateTimeOffset local) =>
        text.AppendLine(local.ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture));

    /// <summary>Сумма без разделителей разрядов: 12000. Минус — обычный дефис.</summary>
    private static string Money(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Signed(int value) => value > 0 ? "+" + Money(value) : Money(value);
}
