namespace NurMarketKassa.Configuration;

/// <summary>Термопринтер ESC/POS через LPT (аналог printer_config + receipt_printer.py).</summary>
public sealed class ReceiptPrinterSettings
{
    /// <summary>Включить физическую печать при успешной оплате с галочкой «print_receipt».</summary>
    public bool Enabled { get; init; }

    /// <summary>LPT1, LPT2 или путь \\.\LPT1</summary>
    public string DevicePath { get; init; } = "LPT1";

    /// <summary>cp866, cp1251, wpc1251, windows-1251 (как RECEIPT_TEXT_ENCODING).</summary>
    public string TextEncoding { get; init; } = "cp866";

    /// <summary>ESC t n; null = по кодировке (17 для CP866, 46 для CP1251).</summary>
    public int? EscPosTableByte { get; init; }

    /// <summary>Опционально ESC R n.</summary>
    public int? EscRByte { get; init; }

    /// <summary>Повторы при сбое LPT (DESKTOP_MARKET_RECEIPT_RETRY).</summary>
    public int RetryCount { get; init; } = 3;
}
