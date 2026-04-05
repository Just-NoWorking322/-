using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>URL превью товара (логика product_media.product_image_url).</summary>
internal static class ProductImageUrl
{
    public static string? TryGet(JsonElement p, string apiBaseUrl)
    {
        if (p.ValueKind != JsonValueKind.Object)
            return null;
        var baseTrim = apiBaseUrl.Trim().TrimEnd('/');

        foreach (var k in new[]
                 {
                     "primary_image_url", "image_url", "thumbnail_url", "photo_url", "picture_url",
                     "main_image_url", "cover_url", "preview_url", "image_path", "media_url",
                 })
        {
            if (p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = ToAbsolute(v.GetString(), baseTrim);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        foreach (var k in new[] { "image", "photo", "thumbnail", "picture", "cover", "img" })
        {
            if (!p.TryGetProperty(k, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = ToAbsolute(v.GetString(), baseTrim);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                foreach (var kk in new[]
                         {
                             "url", "src", "image", "file", "path", "thumbnail", "full", "download_url", "href",
                         })
                {
                    if (v.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        var s = ToAbsolute(u.GetString(), baseTrim);
                        if (!string.IsNullOrEmpty(s))
                            return s;
                    }
                }
            }
        }

        foreach (var nest in new[] { "primary_image", "main_image", "cover_image", "image_data", "photo_data" })
        {
            if (!p.TryGetProperty(nest, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var kk in new[] { "url", "src", "image", "file", "path", "thumbnail", "full", "download_url" })
            {
                if (nested.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                {
                    var s = ToAbsolute(u.GetString(), baseTrim);
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
            }
        }

        foreach (var key in new[] { "images", "photos", "gallery", "media", "attachments" })
        {
            if (!p.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            var n = 0;
            foreach (var it in arr.EnumerateArray())
            {
                if (++n > 8)
                    break;
                if (it.ValueKind == JsonValueKind.String)
                {
                    var s = ToAbsolute(it.GetString(), baseTrim);
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }

                if (it.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kk in new[] { "url", "src", "image", "file", "path", "thumbnail", "full" })
                    {
                        if (it.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            var s = ToAbsolute(u.GetString(), baseTrim);
                            if (!string.IsNullOrEmpty(s))
                                return s;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string ToAbsolute(string? u, string baseNoSlash)
    {
        u = (u ?? "").Trim();
        if (u.Length == 0)
            return "";
        if (u.StartsWith("//", StringComparison.Ordinal))
            return "https:" + u;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return u;
        if (u.StartsWith('/') && baseNoSlash.Length > 0)
            return baseNoSlash + u;
        return u;
    }
}
