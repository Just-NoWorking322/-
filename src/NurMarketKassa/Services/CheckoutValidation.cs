using System.Globalization;

namespace NurMarketKassa.Services;

internal static class CheckoutValidation
{
    public static string NormalizeDecimal(string? raw) => (raw ?? "").Trim().Replace(',', '.');

    /// <summary>null = ок, иначе текст ошибки (как validate_cash_received).</summary>
    public static string? ValidateCashReceived(string? raw, double totalDue)
    {
        var n = NormalizeDecimal(raw);
        if (!double.TryParse(n, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return "Введите сумму «получено наличными»";

        if (v < 0)
            return "Сумма не может быть отрицательной";

        if (totalDue > 0 && v + 1e-9 < totalDue)
            return $"Сумма не меньше к оплате ({totalDue.ToString("0.00", CultureInfo.InvariantCulture)} сом)";

        return null;
    }
}
