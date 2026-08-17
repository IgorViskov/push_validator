using ShopDemo.Domain;
using ShopDemo.Domain.Billing;
using ShopDemo.Domain.Checkout;
using ShopDemo.Domain.Pricing;
using ShopDemo.Domain.Reporting;

var builder = WebApplication.CreateBuilder(args);

// Регистрации DI: единственное место, где известна связь «интерфейс → реализация».
// Из вызывающего кода она не выводится, и граф кода извлекает её именно отсюда.
builder.Services.AddSingleton<IDiscountPolicy, TierDiscountPolicy>();
builder.Services.AddSingleton<IDiscountPolicy, VolumeDiscountPolicy>();
builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
builder.Services.AddSingleton<PriceCalculator>();
builder.Services.AddSingleton<CheckoutService>();
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddSingleton<SalesReportBuilder>();

var app = builder.Build();

app.MapGet("/", () => "ShopDemo — тестовое приложение для ReviewAgent");

// Четвёртый вызывающий PriceCalculator — прямо из обработчика эндпоинта.
app.MapPost("/orders/quote", (Order order, PriceCalculator prices) =>
    Results.Ok(prices.Calculate(order)));

app.MapPost("/orders/checkout", async (Order order, CheckoutService checkout, CancellationToken ct) =>
{
    var result = await checkout.CheckoutAsync(order, ct);
    return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/orders/{id}/invoice", (string id, IOrderRepository orders, InvoiceService invoices,
    CancellationToken ct) => InvoiceFor(id, orders, invoices, ct));

app.MapGet("/reports/sales", (SalesReportBuilder reports, CancellationToken ct) =>
    reports.BuildAsync(ct));

app.Run();

static async Task<IResult> InvoiceFor(
    string id, IOrderRepository orders, InvoiceService invoices, CancellationToken ct)
{
    var all = await orders.ListAsync(ct);
    var order = all.FirstOrDefault(o => o.Id == id);

    return order is null
        ? Results.NotFound(new { error = $"заказ {id} не найден" })
        : Results.Text(invoices.BuildInvoice(order), "text/plain; charset=utf-8");
}
