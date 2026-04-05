namespace NurMarketKassa.Configuration;

/// <summary>COM-весы (аналог переменных DESKTOP_MARKET_SCALE_* в scale_manager.py).</summary>
public sealed class ScaleSettings
{
    public bool Enabled { get; init; }

    /// <summary>Например COM3</summary>
    public string ComPort { get; init; } = "COM3";

    public int BaudRate { get; init; } = 9600;

    /// <summary>Hex байты запроса веса, напр. «05» или «57 0d»; пусто — только чтение.</summary>
    public string? RequestHex { get; init; }

    /// <summary>Интервал повторной отправки запроса (мс), 0 — не слать.</summary>
    public int PollMs { get; init; }
}
