using Smena.Core.Shifts;

namespace Smena.Core.Operations;

/// <summary>
/// Правила операций кассы: выплаты сотрудникам, приход и расход сейфа.
/// Каждая проверка возвращает текст отказа для администратора либо null.
/// </summary>
public static class CashRules
{
    public const int MaxAmount = ShiftCalculator.MaxAmount;
    public const int MaxCommentLength = 500;

    public static string? CheckAmount(int amount) => amount is > 0 and <= MaxAmount
        ? null
        : $"Сумма должна быть от 1 до {MaxAmount}.";

    public static string? CheckComment(string? comment, bool required)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return required ? "Укажите комментарий." : null;
        }

        return comment.Length > MaxCommentLength ? $"Комментарий длиннее {MaxCommentLength} символов." : null;
    }

    /// <summary>
    /// Выплата из сейфа администратором: не больше того, что клуб должен сотруднику,
    /// и не больше того, что лежит в сейфе. Выплата сверх баланса — только root в панели.
    /// </summary>
    public static string? CheckPayout(int amount, int employeeBalance, int safeBalance)
    {
        if (CheckAmount(amount) is { } amountError)
        {
            return amountError;
        }

        if (employeeBalance <= 0)
        {
            return "У сотрудника нет ЗП к выплате.";
        }

        if (amount > employeeBalance)
        {
            return $"Нельзя выдать больше ЗП сотрудника ({employeeBalance}).";
        }

        return CheckSafeHas(amount, safeBalance);
    }

    public static string? CheckSafeExpense(int amount, int safeBalance) =>
        CheckAmount(amount) ?? CheckSafeHas(amount, safeBalance);

    /// <summary>
    /// Выплата, которую проводит root: из сейфа или с банка, при необходимости —
    /// больше, чем клуб должен сотруднику (его баланс уходит в минус).
    /// Сейф и тут не может уйти в минус: наличных, которых нет, не выдать.
    /// </summary>
    public static string? CheckRootPayout(int amount, int employeeBalance, bool allowOverdraft, bool fromSafe, int safeBalance)
    {
        if (CheckAmount(amount) is { } amountError)
        {
            return amountError;
        }

        if (!allowOverdraft && amount > employeeBalance)
        {
            return $"Сумма больше баланса сотрудника ({employeeBalance}). Чтобы выдать сверх баланса, отметьте это явно.";
        }

        return fromSafe ? CheckSafeHas(amount, safeBalance) : null;
    }

    /// <summary>
    /// Доля каждого сотрудника при инвентаризации. Недостача делится поровну; если
    /// не делится нацело, сумма увеличивается до ближайшей делящейся — чтобы ни у
    /// кого не было «плюс рубль» или «минус рубль» (1000 на троих → по 334).
    /// </summary>
    public static int InventoryShare(int totalAmount, int employeeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalAmount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(employeeCount);

        return (int)(((long)totalAmount + employeeCount - 1) / employeeCount);
    }

    private static string? CheckSafeHas(int amount, int safeBalance) => amount > safeBalance
        ? $"В сейфе по учёту только {Math.Max(0, safeBalance)}."
        : null;
}
