namespace OrderService.Api.Infrastructure;

public readonly record struct OrderTotals(
    decimal Subtotal,
    decimal DiscountRate,
    decimal Discount,
    decimal Total);

public static class OrderTotalsCalculator
{
    public static OrderTotals Calculate(IEnumerable<(int Quantity, decimal UnitPrice)> lines)
    {
        var subtotal = 0m;
        foreach (var (qty, price) in lines)
            subtotal += qty * price;

        var rate = DiscountRateFor(subtotal);
        var discount = Math.Round(subtotal * rate, 2);
        return new OrderTotals(subtotal, rate, discount, subtotal - discount);
    }

    private static decimal DiscountRateFor(decimal subtotal) => subtotal switch
    {
        > 500m => 0.10m,
        > 100m => 0.05m,
        _ => 0m,
    };
}
