namespace ShopDemo.Domain;

/// <param name="Sku">Артикул товара.</param>
/// <param name="Quantity">Количество единиц.</param>
/// <param name="UnitPrice">Цена за единицу.</param>
public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice)
{
    public decimal Total => Quantity * UnitPrice;
}

public enum CustomerTier
{
    Regular,
    Silver,
    Gold
}

/// <summary>Заказ покупателя — то, вокруг чего крутится вся расчётная логика.</summary>
public sealed class Order
{
    public required string Id { get; init; }
    public required CustomerTier Tier { get; init; }
    public DateOnly PlacedOn { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    public List<OrderLine> Lines { get; init; } = [];

    /// <summary>Сумма позиций до скидок и доставки.</summary>
    public decimal Subtotal => Lines.Sum(line => line.Total);

    public int ItemCount => Lines.Sum(line => line.Quantity);
}
