using System.IO;
using System.Text;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public static class EscPosSelfCheckPrinter
{
    /// <summary>Самопроверка принтера (как print_printer_self_check_page в receipt_printer.py).</summary>
    public static void PrintSelfCheck(ReceiptPrinterSettings cfg)
    {
        var enc = (cfg.TextEncoding ?? "cp866").Trim().ToLowerInvariant();
        var ts = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
        const int w = 32;
        var lpt = (cfg.DevicePath ?? "LPT1").Trim();
        var lines = new[]
        {
            new string('=', w),
            "  САМОПРОВЕРКА (ПРИЛОЖЕНИЕ)",
            "  NurMarketKassa (C#)",
            new string('=', w),
            $"Дата/время: {ts}",
            new string('-', w),
            "Модель (цель): Cashino EP-200",
            "Версия прошивки: см. отчёт",
            "  принтера (у держ. кнопки)",
            new string('-', w),
            "В отчёте принтера часто:",
            "Код.стр. по умол.: CP936 GBK",
            "Это нормально. Печать из",
            "кассы: ESC % 0 + ESC t 46 +",
            "WPC1251 (байты cp1251).",
            new string('-', w),
            $"Кодировка текста: {enc}",
            new string('-', w),
            $"LPT: {lpt}",
            new string('-', w),
            "Кириллица (тест):",
            "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ",
            "абвгдежзийклмнопрстуфхцчшщъыьэюя",
            new string('-', w),
            "Латиница:",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "abcdefghijklmnopqrstuvwxyz",
            new string('-', w),
            "Цифры: 0123456789",
            "Символы: !\"#$%&'()*+,-./:;<=>?",
            new string('-', w),
            "Штрихкод CODE39 (текстом):",
            "*123456*",
            new string('=', w),
            "Самопроверка завершена.",
            "",
        };
        EscPosTextReceiptPrinter.Print(cfg, string.Join("\n", lines));
    }
}

/// <summary>
/// Печать готового текста на ESC/POS через LPT (упрощённый аналог print_receipt_text в receipt_printer.py).
/// </summary>
public static class EscPosTextReceiptPrinter
{
    /// <summary>DESKTOP_MARKET_RECEIPT_NO_ESC_PCT=1 — не слать ESC % 0.</summary>
    private static bool NoEscPct() =>
        Environment.GetEnvironmentVariable("DESKTOP_MARKET_RECEIPT_NO_ESC_PCT")?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static void Print(ReceiptPrinterSettings cfg, string text)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("Пустой текст чека.");

        PosLogger.Log($"EscPos Print: LPT={(cfg.DevicePath ?? "").Trim()}, encoding={cfg.TextEncoding}, len={raw.Length}",
            "PRINTER");

        var dev = (cfg.DevicePath ?? "").Trim();
        if (dev.Length == 0)
            throw new InvalidOperationException("Не указан принтер (ReceiptPrinter.DevicePath).");

        var path = NormalizeDevicePath(dev);
        var encName = MapToDotNetEncoding(cfg.TextEncoding);
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(encName);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.GetEncoding(866);
        }

        var table = cfg.EscPosTableByte ?? DefaultEscPosTableByte(encName);
        var retries = Math.Clamp(cfg.RetryCount, 1, 8);
        Exception? last = null;

        for (var i = 0; i < retries; i++)
        {
            try
            {
                using var stream = OpenPort(path);
                WriteReceipt(stream, encoding, raw, table, cfg.EscRByte, NoEscPct());
                stream.Flush();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(70 + 60 * i);
            }
        }

        throw new InvalidOperationException($"Не удалось напечатать чек: {last?.Message}", last);
    }

    private static Stream OpenPort(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception ex)
        {
            /* Python receipt_printer: open("LPT1") — иногда без префикса \\.\ срабатывает иначе */
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal) && path.Length > 4)
            {
                try
                {
                    return new FileStream(path[4..], FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                catch
                {
                    /* fall through */
                }
            }

            throw new InvalidOperationException($"Не удалось открыть порт принтера «{path}».", ex);
        }
    }

    private static void WriteReceipt(Stream s, Encoding encoding, string text, int tableByte, int? escRByte, bool noEscPct)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        // ESC @ init
        s.WriteByte(0x1B);
        s.WriteByte(0x40);

        ApplyCodePage(s, tableByte, escRByte, noEscPct);
        // ESC a 0 left align
        s.WriteByte(0x1B);
        s.WriteByte(0x61);
        s.WriteByte(0x00);
        ApplyCodePage(s, tableByte, escRByte, noEscPct);

        foreach (var line in lines)
        {
            var bytes = encoding.GetBytes(line + "\n");
            s.Write(bytes);
        }

        s.WriteByte(0x0A);
        s.WriteByte(0x0A);
        s.WriteByte(0x0A);

        // GS V 0 — отрез (как python-escpos cut часто)
        try
        {
            s.WriteByte(0x1D);
            s.WriteByte(0x56);
            s.WriteByte(0x00);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void ApplyCodePage(Stream s, int tableByte, int? escRByte, bool noEscPct)
    {
        if (!noEscPct)
        {
            s.WriteByte(0x1B);
            s.WriteByte(0x25);
            s.WriteByte(0x00);
        }

        if (escRByte is >= 0 and <= 255)
        {
            s.WriteByte(0x1B);
            s.WriteByte(0x52);
            s.WriteByte((byte)(escRByte.Value & 0xFF));
        }

        s.WriteByte(0x1B);
        s.WriteByte(0x74);
        s.WriteByte((byte)(tableByte & 0xFF));
    }

    private static string NormalizeDevicePath(string raw)
    {
        var d = raw.Trim();
        if (d.StartsWith(@"\\", StringComparison.Ordinal))
            return d;
        if (d.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            return $@"\\.\{d}";
        return d;
    }

    private static string MapToDotNetEncoding(string userEnc)
    {
        var u = (userEnc ?? "cp866").Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return u switch
        {
            "utf-8" or "utf8" => "windows-1251",
            "cp1251" or "windows-1251" or "wpc1251" => "windows-1251",
            "cp855" => "ibm855",
            "cp866" or "ibm866" => "cp866",
            _ => "cp866",
        };
    }

    private static int DefaultEscPosTableByte(string dotnetEncName)
    {
        if (dotnetEncName.Contains("1251", StringComparison.OrdinalIgnoreCase))
            return 46;
        if (string.Equals(dotnetEncName, "ibm855", StringComparison.OrdinalIgnoreCase))
            return 34;
        return 17;
    }
}
