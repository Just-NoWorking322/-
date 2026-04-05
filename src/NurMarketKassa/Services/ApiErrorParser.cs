using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

internal static class ApiErrorParser
{
    public static string Parse(HttpResponseMessage response, string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return $"HTTP {(int)response.StatusCode}";

        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? bodyText;
                if (detail.ValueKind == JsonValueKind.Array)
                {
                    var parts = detail.EnumerateArray().Select(e => e.ToString()).ToArray();
                    return string.Join("; ", parts);
                }
            }

            var pairs = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    pairs.Add($"{prop.Name}: {string.Join(", ", prop.Value.EnumerateArray().Select(x => x.ToString()))}");
                else
                    pairs.Add($"{prop.Name}: {prop.Value}");
            }

            return pairs.Count > 0 ? string.Join("; ", pairs) : bodyText;
        }
        catch (JsonException)
        {
            var raw = bodyText.Trim();
            var low = raw.Length > 500 ? raw.AsSpan(0, 500).ToString().ToLowerInvariant() : raw.ToLowerInvariant();
            if (raw.StartsWith("<!", StringComparison.Ordinal) || low.Contains("<html", StringComparison.Ordinal))
                return $"HTTP {(int)response.StatusCode}: адрес API не найден или неверный путь (ожидался JSON).";
            return string.IsNullOrEmpty(raw) ? $"HTTP {(int)response.StatusCode}" : raw;
        }
    }
}
