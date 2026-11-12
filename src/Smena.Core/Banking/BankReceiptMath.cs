namespace Smena.Core.Banking;

/// <summary>
/// Ожидаемое зачисление в банк: когда и сколько должно прийти. Это только
/// подсказка для подтверждения — в учёт идёт фактически пришедшая сумма.
/// </summary>
public static class BankReceiptMath
{
    /// <summary>Сумма за вычетом комиссии. Комиссия округляется до рубля.</summary>
    /// <returns>null, если процент комиссии у канала не задан.</returns>
    public static int? ExpectedAmount(int amount, decimal? feePercent)
    {
        if (feePercent is not { } percent)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100m);

        var fee = (int)Math.Round(amount * percent / 100m, MidpointRounding.AwayFromZero);
        return amount - fee;
    }

    /// <summary>Момент отправки плюс задержка канала в календарных днях.</summary>
    /// <returns>null, если задержка у канала не задана.</returns>
    public static DateTimeOffset? ExpectedAt(DateTimeOffset sentAt, int? delayDays)
    {
        if (delayDays is not { } days)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(days);
        return sentAt.ToUniversalTime().AddDays(days);
    }

    /// <summary>Проверка суммы, которую root вводит при подтверждении.</summary>
    /// <returns>Текст отказа либо null.</returns>
    public static string? CheckConfirmedAmount(int sentAmount, int confirmedAmount)
    {
        if (confirmedAmount < 0)
        {
            return "Зачисленная сумма не может быть отрицательной.";
        }

        return confirmedAmount > sentAmount
            ? $"Зачислено не может быть больше отправленного ({sentAmount})."
            : null;
    }
}
