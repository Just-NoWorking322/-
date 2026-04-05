using System.IO;
using System.Text.Json;

namespace NurMarketKassa.Services;

public static class OfflinePendingSalesStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "offline_sales_pending.json");

    public static List<OfflineSaleEntry> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<OfflineSaleEntry>();
            return JsonSerializer.Deserialize<List<OfflineSaleEntry>>(File.ReadAllText(FilePath), JsonOpts)
                   ?? new List<OfflineSaleEntry>();
        }
        catch
        {
            return new List<OfflineSaleEntry>();
        }
    }

    public static void SaveAll(List<OfflineSaleEntry> items)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, JsonOpts));
    }

    public static void Append(OfflineSaleEntry entry)
    {
        var all = LoadAll();
        all.Add(entry);
        SaveAll(all);
    }

    public static int PendingCount => LoadAll().Count(s =>
        string.Equals(s.Status, "pending_sync", StringComparison.OrdinalIgnoreCase));
}
