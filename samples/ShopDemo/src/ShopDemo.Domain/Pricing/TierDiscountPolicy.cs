namespace ShopDemo.Domain.Pricing;

/// <summary>Скидка по уровню покупателя.</summary>
public sealed class TierDiscountPolicy : IDiscountPolicy
{
    public string Name => "уровень покупателя";

    public decimal RateFor(Order order) => order.Tier switch
    {
        CustomerTier.Gold => 0.10m,
        CustomerTier.Silver => 0.05m,
        _ => 0m
    };
}
