using System.IO;
using System.Text.Json;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

/// <summary>Локальные настройки POS (%AppData%\NurMarketKassa\user-settings.json).</summary>
public sealed class UserPreferences
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string ScaleComPort { get; set; } = "COM2";

    public int ScaleBaudRate { get; set; } = 9600;

    public bool ScaleEnabled { get; set; }

    public string? ScaleRequestHex { get; set; }

    public int ScalePollMs { get; set; }

    public string ReceiptDevicePath { get; set; } = "LPT1";

    /// <summary>cp866 | cp1251 | wpc1251 …</summary>
    public string ReceiptEncoding { get; set; } = "cp866";

    /// <summary>null = авто по кодировке (как пустой ESC t в Python).</summary>
    public int? ReceiptEscPosTable { get; set; }

    public int? ReceiptEscR { get; set; }

    public bool ReceiptEnabled { get; set; }

    public int ReceiptRetryCount { get; set; } = 3;

    public bool Fullscreen { get; set; } = true;

    public bool Autostart { get; set; }

    public string LastLoginEmail { get; set; } = "";

    public string LastLoginPassword { get; set; } = "";

    public static UserPreferences Instance { get; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "user-settings.json");

    public static void LoadFromDiskAndMergeDefaults(AppSettings appDefaults)
    {
        var p = Instance;
        p.ScaleEnabled = appDefaults.Scale.Enabled;
        p.ScaleComPort = string.IsNullOrWhiteSpace(appDefaults.Scale.ComPort) ? "COM2" : appDefaults.Scale.ComPort.Trim();
        p.ScaleBaudRate = appDefaults.Scale.BaudRate;
        p.ScaleRequestHex = appDefaults.Scale.RequestHex;
        p.ScalePollMs = appDefaults.Scale.PollMs;

        var rp = appDefaults.ReceiptPrinter;
        p.ReceiptDevicePath = string.IsNullOrWhiteSpace(rp.DevicePath) ? "LPT1" : rp.DevicePath.Trim();
        p.ReceiptEncoding = string.IsNullOrWhiteSpace(rp.TextEncoding) ? "cp866" : rp.TextEncoding.Trim();
        p.ReceiptEscPosTable = rp.EscPosTableByte;
        p.ReceiptEscR = rp.EscRByte;
        p.ReceiptEnabled = rp.Enabled;
        p.ReceiptRetryCount = rp.RetryCount;

        try
        {
            if (!File.Exists(FilePath))
                return;
            var json = File.ReadAllText(FilePath);
            var fromFile = JsonSerializer.Deserialize<UserPreferencesDto>(json, JsonOpt);
            if (fromFile == null)
                return;
            if (!string.IsNullOrWhiteSpace(fromFile.ScaleComPort))
                p.ScaleComPort = fromFile.ScaleComPort.Trim();
            if (fromFile.ScaleBaudRate is > 0)
                p.ScaleBaudRate = fromFile.ScaleBaudRate.Value;
            p.ScaleEnabled = fromFile.ScaleEnabled ?? p.ScaleEnabled;
            p.ScaleRequestHex = fromFile.ScaleRequestHex ?? p.ScaleRequestHex;
            if (fromFile.ScalePollMs is >= 0)
                p.ScalePollMs = fromFile.ScalePollMs.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.ReceiptDevicePath))
                p.ReceiptDevicePath = fromFile.ReceiptDevicePath.Trim();
            if (!string.IsNullOrWhiteSpace(fromFile.ReceiptEncoding))
                p.ReceiptEncoding = fromFile.ReceiptEncoding.Trim();
            p.ReceiptEscPosTable = fromFile.ReceiptEscPosTable ?? p.ReceiptEscPosTable;
            p.ReceiptEscR = fromFile.ReceiptEscR ?? p.ReceiptEscR;
            if (fromFile.ReceiptEnabled is not null)
                p.ReceiptEnabled = fromFile.ReceiptEnabled.Value;
            if (fromFile.ReceiptRetryCount is > 0)
                p.ReceiptRetryCount = fromFile.ReceiptRetryCount.Value;
            if (fromFile.Fullscreen is not null)
                p.Fullscreen = fromFile.Fullscreen.Value;
            if (fromFile.Autostart is not null)
                p.Autostart = fromFile.Autostart.Value;
            if (!string.IsNullOrEmpty(fromFile.LastLoginEmail))
                p.LastLoginEmail = fromFile.LastLoginEmail!;
            if (fromFile.LastLoginPassword is not null)
                p.LastLoginPassword = fromFile.LastLoginPassword;
        }
        catch
        {
            /* ignore */
        }
    }

    public void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var dto = new UserPreferencesDto
            {
                ScaleComPort = ScaleComPort,
                ScaleBaudRate = ScaleBaudRate,
                ScaleEnabled = ScaleEnabled,
                ScaleRequestHex = ScaleRequestHex,
                ScalePollMs = ScalePollMs,
                ReceiptDevicePath = ReceiptDevicePath,
                ReceiptEncoding = ReceiptEncoding,
                ReceiptEscPosTable = ReceiptEscPosTable,
                ReceiptEscR = ReceiptEscR,
                ReceiptEnabled = ReceiptEnabled,
                ReceiptRetryCount = ReceiptRetryCount,
                Fullscreen = Fullscreen,
                Autostart = Autostart,
                LastLoginEmail = LastLoginEmail,
                LastLoginPassword = LastLoginPassword,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, JsonOpt));
        }
        catch
        {
            /* ignore */
        }
    }

    public ScaleSettings ToScaleSettings() =>
        new()
        {
            Enabled = ScaleEnabled,
            ComPort = ScaleComPort,
            BaudRate = ScaleBaudRate,
            RequestHex = ScaleRequestHex,
            PollMs = ScalePollMs,
        };

    public ReceiptPrinterSettings ToReceiptPrinterSettings() =>
        new()
        {
            Enabled = ReceiptEnabled,
            DevicePath = ReceiptDevicePath,
            TextEncoding = ReceiptEncoding,
            EscPosTableByte = ReceiptEscPosTable,
            EscRByte = ReceiptEscR,
            RetryCount = ReceiptRetryCount,
        };

    private sealed class UserPreferencesDto
    {
        public string? ScaleComPort { get; set; }
        public int? ScaleBaudRate { get; set; }
        public bool? ScaleEnabled { get; set; }
        public string? ScaleRequestHex { get; set; }
        public int? ScalePollMs { get; set; }
        public string? ReceiptDevicePath { get; set; }
        public string? ReceiptEncoding { get; set; }
        public int? ReceiptEscPosTable { get; set; }
        public int? ReceiptEscR { get; set; }
        public bool? ReceiptEnabled { get; set; }
        public int? ReceiptRetryCount { get; set; }
        public bool? Fullscreen { get; set; }
        public bool? Autostart { get; set; }
        public string? LastLoginEmail { get; set; }
        public string? LastLoginPassword { get; set; }
    }
}
