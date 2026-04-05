using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Разбор корзины и строк чека по полям API (как в main.py).</summary>
internal static class CartDisplayHelper
{
    public static string? TryCartId(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return null;
        if (!cart.TryGetProperty("id", out var id))
            return null;
        return JsonScalarToString(id);
    }

    /// <summary>shift_id или shift.id из корзины POS.</summary>
    public static string? TryShiftIdFromCart(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return null;
        if (cart.TryGetProperty("shift_id", out var sid))
        {
            var s = JsonScalarToString(sid);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (!cart.TryGetProperty("shift", out var sh))
            return null;
        if (sh.ValueKind == JsonValueKind.Object && sh.TryGetProperty("id", out var id))
            return JsonScalarToString(id);
        return JsonScalarToString(sh);
    }

    /// <summary>Ответ POST …/shifts/open/.</summary>
    public static string? TryShiftIdFromOpenResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("id", out var id))
        {
            var s = JsonScalarToString(id);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (root.TryGetProperty("shift", out var sh) && sh.ValueKind == JsonValueKind.Object &&
            sh.TryGetProperty("id", out var sid))
            return JsonScalarToString(sid);

        return null;
    }

    public static IEnumerable<JsonElement> EnumerateItems(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            yield break;

        if (cart.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in it.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                    yield return el;
            }

            yield break;
        }

        if (cart.TryGetProperty("cart_items", out var ci) && ci.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in ci.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                    yield return el;
            }
        }
    }

    public static string? FirstCashboxId(JsonElement data) =>
        TryFirstCashbox(data, out var id, out _) ? id : null;

    /// <summary>Первая касса в списке: id и отображаемое имя (не UUID).</summary>
    public static bool TryFirstCashbox(JsonElement data, out string? id, out string? displayName)
    {
        id = null;
        displayName = null;
        foreach (var el in UnwrapListElements(data))
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;
            var i = TryCashboxId(el);
            if (string.IsNullOrEmpty(i))
                continue;
            id = i;
            displayName = TryCashboxDisplayName(el) ?? i;
            return true;
        }

        return false;
    }

    public static string? TryCashboxDisplayName(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[]
                 {
                     "name", "title", "label", "display_name", "code", "cashbox_name", "number", "short_name",
                 })
        {
            if (c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> UnwrapListElements(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                yield return el;
            yield break;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                yield return el;
        }
    }

    private static string? TryCashboxId(JsonElement c)
    {
        foreach (var key in new[] { "id", "pk", "uuid" })
        {
            if (!c.TryGetProperty(key, out var v))
                continue;
            var s = JsonScalarToString(v);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        return null;
    }

    public static string ItemName(JsonElement it)
    {
        if (it.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            var n = NameFromProductDict(p);
            if (!string.IsNullOrEmpty(n))
                return n;
        }

        if (it.TryGetProperty("product_snapshot", out var snap) && snap.ValueKind == JsonValueKind.Object)
        {
            var n = NameFromProductDict(snap);
            if (!string.IsNullOrEmpty(n))
                return n;
        }

        foreach (var key in new[]
                 {
                     "product_name", "name", "title", "display_name", "label", "item_name", "description",
                 })
        {
            if (it.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        if (it.TryGetProperty("product_id", out var pid))
        {
            var ps = JsonScalarToString(pid);
            if (!string.IsNullOrEmpty(ps))
                return $"Товар #{ps}";
        }

        return "—";
    }

    private static string? NameFromProductDict(JsonElement p)
    {
        foreach (var key in new[] { "name", "title", "display_name", "label" })
        {
            if (p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        return null;
    }

    public static string QuantityPriceLine(JsonElement it)
    {
        var qty = TryDouble(it, "quantity") ?? 1;
        var up = TryDouble(it, "unit_price") ?? 0;
        return $"{FormatMoney(qty)} × {FormatMoney(up)} сом";
    }

    public static double UnitPrice(JsonElement it) => TryDouble(it, "unit_price") ?? 0;

    public static string LineTotal(JsonElement it)
    {
        foreach (var key in new[]
                 {
                     "line_total", "line_total_amount", "line_amount", "amount", "total", "sum",
                     "total_price", "line_total_display", "subtotal", "line_sum", "total_sum",
                 })
        {
            if (TryDouble(it, key) is { } v)
                return FormatMoney(v);
        }

        try
        {
            var q = TryDouble(it, "quantity") ?? 0;
            var up = TryDouble(it, "unit_price") ?? 0;
            var disc = TryDouble(it, "discount_total")
                       ?? TryDouble(it, "line_discount")
                       ?? TryDouble(it, "discount")
                       ?? 0;
            if (q > 0 && up >= 0)
                return FormatMoney(q * up - disc);
        }
        catch
        {
            /* fall through */
        }

        return FormatMoney(0);
    }

    public static double TotalDue(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var src in new[] { cart, TryTotals(cart) })
        {
            if (src.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var key in new[]
                     {
                         "total", "grand_total", "total_amount", "amount_due", "payable_total",
                         "order_total", "total_to_pay", "amount_total",
                     })
            {
                if (TryDouble(src, key) is { } v)
                    return v;
            }
        }

        return 0;
    }

    private static JsonElement TryTotals(JsonElement cart) =>
        cart.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;

    public static string FormatMoney(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static double? TryDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return null;
        return JsonNumberToDouble(v);
    }

    private static double? JsonNumberToDouble(JsonElement v)
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

    private static string? JsonScalarToString(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    public static string? TryItemId(JsonElement it) =>
        it.ValueKind == JsonValueKind.Object && it.TryGetProperty("id", out var id) ? JsonScalarToString(id) : null;

    /// <summary>ID товара для POST add-item: product_id или product.id.</summary>
    public static string? TryProductId(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return null;
        if (it.TryGetProperty("product_id", out var pid))
        {
            var s = JsonScalarToString(pid);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (it.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "id", "pk", "uuid" })
            {
                if (!p.TryGetProperty(key, out var id))
                    continue;
                var s = JsonScalarToString(id);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        return null;
    }

    public static double LineQuantity(JsonElement it) => TryDouble(it, "quantity") ?? 1.0;

    /// <summary>Параметр discount_total для add-item, если в строке была скидка.</summary>
    public static string? OptionalDiscountTotalParam(JsonElement it)
    {
        var d = TryDouble(it, "discount_total")
                ?? TryDouble(it, "line_discount")
                ?? TryDouble(it, "discount")
                ?? 0;
        return d > 1e-6 ? FormatMoney(d) : null;
    }

    /// <summary>Шаг 0.1 (кг) или 1 (шт) — как _cart_line_must_weigh без локального _pos_unit_mode.</summary>
    public static bool LineMustWeigh(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return false;

        if (TruthyBool(it, "is_wait") || TruthyBool(it, "is_weight"))
            return true;

        if (DictHasKgUnit(it))
            return true;

        if (it.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object && ProductMustWeigh(p))
            return true;

        if (it.TryGetProperty("product_snapshot", out var s) && s.ValueKind == JsonValueKind.Object && ProductMustWeigh(s))
            return true;

        return NameLooksWeighed(ItemName(it));
    }

    private static readonly string[] WeightNameHints =
    {
        "карто", "картоф", "помид", "томат", "огур", "лук", "морков", "капуст",
        "яблок", "банан", "апельсин", "груш", "перец", "свекл", "свёкл",
    };

    private static bool NameLooksWeighed(string name)
    {
        var raw = name.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw))
            return false;
        return WeightNameHints.Any(h => raw.Contains(h, StringComparison.Ordinal));
    }

    /// <summary>Как _product_must_weigh в main.py — для каталога и диалога взвешивания.</summary>
    public static bool ProductMustWeigh(JsonElement p) =>
        TruthyBool(p, "is_wait") || TruthyBool(p, "is_weight") || DictHasKgUnit(p);

    private static bool DictHasKgUnit(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in new[] { "unit", "unit_display", "measure_unit", "sale_unit", "uom" })
        {
            if (d.TryGetProperty(key, out var u) && UnitIsKg(u))
                return true;
        }

        return false;
    }

    private static bool UnitIsKg(JsonElement unit)
    {
        if (unit.ValueKind != JsonValueKind.String)
            return false;
        var raw = unit.GetString()?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(raw))
            return false;
        var compact = raw.Replace(" ", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        if (compact is "кг" or "kg" or "kг" or "kilogram" or "kilograms")
            return true;
        if (raw.Contains("килограм", StringComparison.Ordinal))
            return true;
        if (compact.EndsWith("кг", StringComparison.Ordinal) || raw.EndsWith(" kg", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static bool TruthyBool(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.True)
            return true;
        if (v.ValueKind == JsonValueKind.False)
            return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim().ToLowerInvariant();
            return s is "1" or "true" or "yes" or "on";
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return Math.Abs(d) > double.Epsilon;
        return false;
    }
}
