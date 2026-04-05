using System.Globalization;

namespace NurMarketKassa.Services;

internal static class OrderDiscountHelper
{
    public static string NormalizeDecimal(string raw) => (raw ?? "").Trim().Replace(',', '.');

    /// <summary>Пустая строка или число ≈0 — поле считается «не задано» (второе поле с 0.00 не мешает %).</summary>
    public static bool IsEmptyOrZeroLike(string? raw)
    {
        var n = NormalizeDecimal(raw ?? "");
        if (n.Length == 0)
            return true;
        if (!double.TryParse(n, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) ||
            double.IsInfinity(v))
            return false;
        return Math.Abs(v) < 1e-9;
    }

    public static string? ValidatePercent(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return "Введите процент";
        if (!TryParseNonNegative(s, out var v))
            return "Некорректный процент";
        if (v > 100)
            return "Процент не может быть больше 100";
        return null;
    }

    public static string? ValidateSum(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return "Введите сумму скидки";
        if (!TryParseNonNegative(s, out _))
            return "Некорректная сумма скидки";
        return null;
    }

    public static string? ValidateQuantity(string raw)
    {
        if (!TryParseNonNegative(NormalizeDecimal(raw), out var v))
            return "Некорректное количество";
        if (v <= 0)
            return "Количество должно быть больше нуля";
        if (v > 1_000_000)
            return "Слишком большое количество";
        return null;
    }

    private static bool TryParseNonNegative(string s, out double v)
    {
        v = 0;
        s = NormalizeDecimal(s);
        if (s.Length == 0)
            return false;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v >= 0 && !double.IsNaN(v) &&
               !double.IsInfinity(v);
    }
}
