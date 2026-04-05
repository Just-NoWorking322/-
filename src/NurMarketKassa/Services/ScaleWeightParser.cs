using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>parse_weight_line из scale_manager.py</summary>
internal static partial class ScaleWeightParser
{
    [GeneratedRegex(@"[-+]?\d+(?:[.,]\d+)?")]
    private static partial Regex WeightTokenRegex();

    public static double? ParseWeightLine(ReadOnlySpan<byte> raw)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(raw).Trim();
        }
        catch
        {
            text = Encoding.Latin1.GetString(raw).Trim();
        }

        return ParseWeightLine(text);
    }

    public static double? ParseWeightLine(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var replaced = text.Replace(',', '.');
        var norm = Regex.Replace(replaced, @"[^\d.\-+eE]", " ");
        norm = Regex.Replace(norm, @"\s+", " ").Trim();
        var matches = WeightTokenRegex().Matches(norm);
        if (matches.Count == 0)
        {
            var compact = norm.Replace(" ", "", StringComparison.Ordinal);
            matches = Regex.Matches(compact, @"[-+]?\d+\.?\d*", RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var candidate = matches[i].Value.Replace(',', '.');
            if (double.TryParse(candidate, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return v;
        }

        return null;
    }
}
