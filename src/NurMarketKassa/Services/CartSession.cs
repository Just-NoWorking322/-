using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Текущая POS-корзина в памяти (аналог session["cart"] / cart_id).</summary>
public sealed class CartSession : IDisposable
{
    private JsonDocument? _doc;

    public string? CartId { get; private set; }

    public JsonElement Root => _doc?.RootElement ?? default;

    /// <summary>Есть данные корзины для отображения.</summary>
    public bool HasCart => _doc != null;

    /// <summary>Можно вызвать GET /pos/carts/{id}/.</summary>
    public bool CanRefresh => !string.IsNullOrEmpty(CartId);

    public void SetCart(JsonElement root)
    {
        _doc?.Dispose();
        CartId = CartDisplayHelper.TryCartId(root);
        _doc = JsonDocument.Parse(root.GetRawText());
    }

    public void Clear()
    {
        _doc?.Dispose();
        _doc = null;
        CartId = null;
    }

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }
}
