namespace ShopDemo.Domain.Pricing;

/// <summary>Скидка за объём: чем больше единиц в заказе, тем выше доля.</summary>
public sealed class VolumeDiscountPolicy : IDiscountPolicy
{
    public string Name => "объём заказа";

    public decimal RateFor(Order order) => order.ItemCount switch
    {
        >= 50 => 0.12m,
        >= 20 => 0.07m,
        >= 10 => 0.03m,
        _ => 0m
    };
}
