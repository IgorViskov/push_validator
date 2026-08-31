namespace ShopDemo.Domain.Checkout;

/// <summary>Хранилище заказов. Реализация подставляется через контейнер.</summary>
public interface IOrderRepository
{
    Task SaveAsync(Order order, decimal amountDue, CancellationToken ct);

    Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct);
}

/// <summary>Хранилище в памяти — демонстрационному приложению большего не нужно.</summary>
public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly List<Order> _orders = [];
    private readonly Lock _gate = new();

    public Task SaveAsync(Order order, decimal amountDue, CancellationToken ct)
    {
        lock (_gate) _orders.Add(order);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Order>>(_orders.ToArray());
    }
}
