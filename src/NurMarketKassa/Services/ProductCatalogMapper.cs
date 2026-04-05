using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

internal static class ProductCatalogMapper
{
    public static CatalogProductTileVm? TryTile(JsonElement p, string apiBaseUrl)
    {
        var id = TryId(p);
        if (string.IsNullOrEmpty(id))
            return null;
        var title = Title(p);
        var price = TryPrice(p);
        var priceLine = price is null ? "—" : $"{price.Value.ToString("0.00", CultureInfo.InvariantCulture)} сом";
        var weigh = CartDisplayHelper.ProductMustWeigh(p);
        var img = ProductImageUrl.TryGet(p, apiBaseUrl);
        return new CatalogProductTileVm(id, title, priceLine, weigh, img);
    }

    public static string Title(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
            return "—";
        foreach (var k in new[] { "name", "title", "display_name", "label" })
        {
            if (p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        var id = TryId(p);
        return string.IsNullOrEmpty(id) ? "—" : $"Товар #{id}";
    }

    public static string? TryId(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id))
            return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }

    public static double? TryPrice(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("price", out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)
                ? x
                : null,
            _ => null,
        };
    }
}
