using System.IO;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

public static class DeferredCartsStore
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
            "deferred_carts.json");

    public static List<DeferredCartEntry> LoadAll()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new List<DeferredCartEntry>();
            return JsonSerializer.Deserialize<List<DeferredCartEntry>>(File.ReadAllText(path), JsonOpts)
                   ?? new List<DeferredCartEntry>();
        }
        catch
        {
            return new List<DeferredCartEntry>();
        }
    }

    public static void SaveAll(List<DeferredCartEntry> items)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, JsonOpts));
    }

    public static void Add(DeferredCartEntry entry)
    {
        var all = LoadAll();
        all.Add(entry);
        SaveAll(all);
    }

    public static void RemoveIds(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        SaveAll(LoadAll().Where(x => !set.Contains(x.Id)).ToList());
    }
}
