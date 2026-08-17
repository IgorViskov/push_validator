namespace ShopDemo.Domain.Pricing;

/// <param name="Subtotal">Сумма позиций до скидок.</param>
/// <param name="DiscountRate">Итоговая доля скидки, 0..1.</param>
/// <param name="Total">Сумма к оплате.</param>
/// <param name="AppliedPolicies">Какие правила скидок сработали — попадает в чек.</param>
public sealed record PriceBreakdown(
    decimal Subtotal,
    decimal DiscountRate,
    decimal Total,
    IReadOnlyList<string> AppliedPolicies);

/// <summary>
/// Расчёт суммы заказа. Центральный тип домена: его вызывают оформление заказа,
/// выставление счёта и отчётность, — поэтому изменение его контракта задевает
/// сразу несколько мест, и по диффу одного файла этого не видно.
/// </summary>
public sealed class PriceCalculator
{
    /// <summary>
    /// Потолок суммарной скидки. Правила складываются, и без потолка три сработавших
    /// правила уводят сумму в минус — заказ становится «доплатой покупателю».
    /// </summary>
    public const decimal MaxDiscountRate = 0.25m;

    private readonly IReadOnlyList<IDiscountPolicy> _policies;

    public PriceCalculator(IEnumerable<IDiscountPolicy> policies) => _policies = policies.ToArray();

    /// <summary>Сумма к оплате с разбивкой по сработавшим правилам скидок.</summary>
    public PriceBreakdown Calculate(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var applied = new List<string>();
        var rate = 0m;

        foreach (var policy in _policies)
        {
            var policyRate = policy.RateFor(order);
            if (policyRate <= 0) continue;

            rate += policyRate;
            applied.Add($"{policy.Name}: {policyRate:P0}");
        }

        rate = Math.Min(rate, MaxDiscountRate);

        return new PriceBreakdown(
            Subtotal: order.Subtotal,
            DiscountRate: rate,
            Total: decimal.Round(order.Subtotal * (1 - rate), 2),
            AppliedPolicies: applied);
    }

    /// <summary>Только сумма к оплате — короткий путь для тех, кому разбивка не нужна.</summary>
    public decimal CalculateTotal(Order order) => Calculate(order).Total;
}
