namespace NurMarketKassa.Services;

/// <summary>Оффлайн-продажа для последующей выгрузки на сервер (как очередь в PRINTER_EXE_CHAIN).</summary>
public sealed class OfflineSaleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>pending_sync | synced (после ручной/будущей синхронизации).</summary>
    public string Status { get; set; } = "pending_sync";

    public string PaymentMethod { get; set; } = "";

    public string? CashReceived { get; set; }

    public string CartJson { get; set; } = "{}";
}
