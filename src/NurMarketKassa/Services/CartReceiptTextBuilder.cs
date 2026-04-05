using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

internal static class CartReceiptTextBuilder
{
    internal static string BuildSimpleReceipt(string cartJson, string titleLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            var root = doc.RootElement;
            var lines = new List<string>
            {
                titleLine,
                "",
                "Nur Market — касса",
                DateTime.Now.ToString("g", CultureInfo.CurrentCulture),
                "--------------------------------",
            };

            foreach (var it in CartDisplayHelper.EnumerateItems(root))
            {
                lines.Add(CartDisplayHelper.ItemName(it));
                lines.Add($"  {CartDisplayHelper.QuantityPriceLine(it)}  → {CartDisplayHelper.LineTotal(it)} сом");
            }

            lines.Add("--------------------------------");
            lines.Add($"Итого: {CartDisplayHelper.FormatMoney(CartDisplayHelper.TotalDue(root))} сом");
            return string.Join("\n", lines);
        }
        catch
        {
            return titleLine + "\n(не удалось разобрать корзину)";
        }
    }
}
