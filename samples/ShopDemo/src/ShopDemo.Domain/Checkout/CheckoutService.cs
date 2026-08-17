using ShopDemo.Domain.Pricing;

namespace ShopDemo.Domain.Checkout;

/// <param name="Accepted">Принят ли заказ к оплате.</param>
public sealed record CheckoutResult(bool Accepted, decimal AmountDue, string Message);

/// <summary>Оформление заказа — первый из вызывающих <see cref="PriceCalculator"/>.</summary>
public sealed class CheckoutService
{
    private readonly PriceCalculator _prices;
    private readonly IOrderRepository _orders;

    public CheckoutService(PriceCalculator prices, IOrderRepository orders)
    {
        _prices = prices;
        _orders = orders;
    }

    public async Task<CheckoutResult> CheckoutAsync(Order order, CancellationToken ct)
    {
        if (order.Lines.Count == 0)
            return new CheckoutResult(false, 0, "Заказ пуст.");

        var amount = _prices.CalculateTotal(order);

        if (amount <= 0)
            return new CheckoutResult(false, amount, "Сумма заказа неположительная — расчёт неверен.");

        await _orders.SaveAsync(order, amount, ct);

        return new CheckoutResult(true, amount, $"Заказ {order.Id} принят на сумму {amount:C}.");
    }
}
