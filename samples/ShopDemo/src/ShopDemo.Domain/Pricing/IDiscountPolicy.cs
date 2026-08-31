namespace ShopDemo.Domain.Pricing;

/// <summary>
/// Правило скидки. Реализации подставляются через контейнер, поэтому связь
/// «интерфейс → реализация» существует только в регистрации DI: синтаксически
/// из вызывающего кода её не видно.
/// </summary>
public interface IDiscountPolicy
{
    string Name { get; }

    /// <summary>Скидка на сумму заказа: доля от 0 до 1.</summary>
    decimal RateFor(Order order);
}
