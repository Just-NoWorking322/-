namespace NurMarketKassa.Models.Pos;

public sealed class CartLineRow
{
    public string ItemId { get; init; } = "";
    public double Qty { get; init; }
    public bool WeighedLine { get; init; }
    public string Title { get; init; } = "";
    public string SubLine { get; init; } = "";
    public string LineTotal { get; init; } = "";

    /// <summary>Для весовых строк — подпись «30.00 сом/кг» в диалоге взвешивания.</summary>
    public string PricePerKgHint { get; init; } = "";
}
