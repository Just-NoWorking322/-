using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NurMarketKassa.Services;

/// <summary>Дисковый кэш превью + загрузка с Bearer для URL API.</summary>
internal sealed class ProductThumbService
{
    private readonly string _cacheDir;

    public ProductThumbService()
    {
        _cacheDir = Path.Combine(AppContext.BaseDirectory, "product_thumbs");
        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch
        {
            /* ignore */
        }
    }

    public async Task SetThumbAsync(
        Dispatcher uiDispatcher,
        NurMarketApiClient api,
        string apiBaseUrl,
        string imageUrl,
        Models.Pos.CatalogProductTileVm vm,
        CancellationToken ct)
    {
        var path = await GetOrDownloadPathAsync(api, apiBaseUrl, imageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        await uiDispatcher.InvokeAsync(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                vm.Thumb = bmp;
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private async Task<string?> GetOrDownloadPathAsync(
        NurMarketApiClient api,
        string apiBaseUrl,
        string imageUrl,
        CancellationToken ct)
    {
        var key = Sha256Hex(imageUrl);
        var ext = GuessExt(imageUrl);
        var local = Path.Combine(_cacheDir, key + ext);
        if (File.Exists(local))
            return local;

        byte[]? bytes;
        if (IsSameHost(imageUrl, apiBaseUrl))
            bytes = await api.DownloadAuthorizedAsync(imageUrl, ct).ConfigureAwait(false);
        else
            bytes = await DownloadPublicAsync(imageUrl, ct).ConfigureAwait(false);

        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            await File.WriteAllBytesAsync(local, bytes, ct).ConfigureAwait(false);
            return local;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameHost(string imageUrl, string apiBaseUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var iu))
            return false;
        var b = (apiBaseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0)
            return false;
        if (!Uri.TryCreate(b + "/", UriKind.Absolute, out var bu))
            return false;
        return string.Equals(iu.Host, bu.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> DownloadPublicAsync(string url, CancellationToken ct)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            return await c.GetByteArrayAsync(new Uri(url), ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string GuessExt(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains(".png", StringComparison.Ordinal))
            return ".png";
        if (u.Contains(".webp", StringComparison.Ordinal))
            return ".webp";
        if (u.Contains(".gif", StringComparison.Ordinal))
            return ".gif";
        return ".jpg";
    }
}
