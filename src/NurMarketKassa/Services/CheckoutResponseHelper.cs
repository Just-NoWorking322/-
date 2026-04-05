using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

internal static class CheckoutResponseHelper
{
    public static string FormatSuccess(JsonElement res)
    {
        var ch = TryChangeAmount(res);
        var saleId = TrySaleId(res);
        var msg = ch != null
            ? $"Оплата прошла. Сдача: {FormatMoney(ch.Value)} сом"
            : "Оплата прошла";
        if (!string.IsNullOrEmpty(saleId))
            msg += $" (продажа {TruncateId(saleId)})";
        return msg;
    }

    private static string TruncateId(string id)
    {
        var s = id.Trim();
        return s.Length <= 8 ? s : s[..8] + "…";
    }

    private static double? TryChangeAmount(JsonElement root)
    {
        if (TryChangeInObject(root) is { } v)
            return v;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) &&
            d.ValueKind == JsonValueKind.Object)
        {
            if (TryChangeInObject(d) is { } v2)
                return v2;
        }

        return null;
    }

    private static double? TryChangeInObject(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "change", "change_amount", "cash_change", "amount_change" })
        {
            if (!obj.TryGetProperty(key, out var p))
                continue;
            if (TryDouble(p) is { } x)
                return x;
        }

        return null;
    }

    internal static string? TrySaleId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "sale_id", "order_id" })
        {
            if (root.TryGetProperty(key, out var p))
            {
                var s = JsonScalar(p);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        if (root.TryGetProperty("sale", out var s2) && s2.ValueKind == JsonValueKind.Object &&
            s2.TryGetProperty("id", out var id))
            return JsonScalar(id);

        return null;
    }

    private static double? TryDouble(JsonElement v)
    {
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)
                ? x
                : null,
            _ => null,
        };
    }

    private static string? JsonScalar(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    private static string FormatMoney(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Текст чека из ответа checkout при print_receipt=true.</summary>
    internal static string? TryReceiptTextFromCheckout(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "receipt_text", "receipt", "text", "content" })
        {
            if (root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var t = p.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                    return t;
            }
        }

        foreach (var nestKey in new[] { "data", "sale", "result" })
        {
            if (!root.TryGetProperty(nestKey, out var nest) || nest.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var key in new[] { "receipt_text", "receipt", "text" })
            {
                if (nest.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var t = p.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                        return t;
                }
            }
        }

        return null;
    }

    /// <summary>Текст из GET …/pos/sales/…/receipt/.</summary>
    internal static string? TryReceiptTextFromSaleReceiptPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "receipt_text", "text", "body", "content", "plain" })
        {
            if (root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var t = p.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                    return t;
            }
        }

        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var el in lines.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                    parts.Add(el.GetString() ?? "");
            }

            if (parts.Count > 0)
                return string.Join("\n", parts);
        }

        JsonElement nested = default;
        if (root.TryGetProperty("receipt", out var r) && r.ValueKind == JsonValueKind.Object)
            nested = r;
        else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            nested = d;

        if (nested.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "text", "body", "receipt_text", "plain" })
            {
                if (nested.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var t = p.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                        return t;
                }
            }
        }

        return null;
    }
}
