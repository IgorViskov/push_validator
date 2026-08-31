using ShopDemo.Domain.Checkout;
using ShopDemo.Domain.Pricing;

namespace ShopDemo.Domain.Reporting;

/// <param name="Orders">Сколько заказов попало в отчёт.</param>
/// <param name="Revenue">Выручка с учётом скидок.</param>
public sealed record SalesReport(int Orders, decimal Revenue, decimal AverageOrder);

/// <summary>
/// Отчётность — третий из вызывающих <see cref="PriceCalculator"/>, и самый неочевидный:
/// вызов спрятан внутри LINQ-выражения.
/// </summary>
public sealed class SalesReportBuilder
{
    private readonly PriceCalculator _prices;
    private readonly IOrderRepository _orders;

    public SalesReportBuilder(PriceCalculator prices, IOrderRepository orders)
    {
        _prices = prices;
        _orders = orders;
    }

    public async Task<SalesReport> BuildAsync(CancellationToken ct)
    {
        var orders = await _orders.ListAsync(ct);
        if (orders.Count == 0) return new SalesReport(0, 0, 0);

        var revenue = orders.Sum(order => _prices.CalculateTotal(order));

        return new SalesReport(
            Orders: orders.Count,
            Revenue: revenue,
            AverageOrder: decimal.Round(revenue / orders.Count, 2));
    }
}
