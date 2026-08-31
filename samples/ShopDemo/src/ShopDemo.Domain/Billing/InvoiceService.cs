using ShopDemo.Domain.Pricing;

namespace ShopDemo.Domain.Billing;

/// <summary>
/// Выставление счёта — второй из вызывающих <see cref="PriceCalculator"/>. Лежит
/// в другом каталоге и в диффе правки калькулятора не появляется вовсе.
/// </summary>
public sealed class InvoiceService
{
    private readonly PriceCalculator _prices;

    public InvoiceService(PriceCalculator prices) => _prices = prices;

    public string BuildInvoice(Order order)
    {
        var breakdown = _prices.Calculate(order);

        var lines = new List<string>
        {
            $"Счёт по заказу {order.Id}",
            $"Позиций: {order.Lines.Count}, единиц: {order.ItemCount}",
            $"Сумма до скидок: {breakdown.Subtotal:C}"
        };

        foreach (var policy in breakdown.AppliedPolicies)
            lines.Add($"  скидка — {policy}");

        lines.Add($"Итого к оплате: {breakdown.Total:C}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Итог для платёжного шлюза — ему нужна только сумма.</summary>
    public decimal AmountForPayment(Order order) => _prices.CalculateTotal(order);
}
