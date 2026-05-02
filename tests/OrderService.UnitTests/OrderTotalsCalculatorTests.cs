using FluentAssertions;
using OrderService.Api.Infrastructure;

namespace OrderService.UnitTests;

public class OrderTotalsCalculatorTests
{
    [Fact]
    public void NoItems_AreZero()
    {
        var t = OrderTotalsCalculator.Calculate(Array.Empty<(int, decimal)>());

        t.Subtotal.Should().Be(0);
        t.Discount.Should().Be(0);
        t.Total.Should().Be(0);
    }

    [Fact]
    public void Below100_NoDiscount()
    {
        var t = OrderTotalsCalculator.Calculate(new[] { (2, 15.50m) });

        t.Subtotal.Should().Be(31.00m);
        t.DiscountRate.Should().Be(0m);
        t.Discount.Should().Be(0m);
        t.Total.Should().Be(31.00m);
    }

    [Theory]
    [InlineData(100.01, 0.05)]
    [InlineData(250, 0.05)]
    [InlineData(500, 0.05)]
    [InlineData(500.01, 0.10)]
    [InlineData(1000, 0.10)]
    public void DiscountTier_AppliesAtCorrectThreshold(decimal subtotal, decimal expectedRate)
    {
        var t = OrderTotalsCalculator.Calculate(new[] { (1, subtotal) });

        t.DiscountRate.Should().Be(expectedRate);
        t.Discount.Should().Be(Math.Round(subtotal * expectedRate, 2));
        t.Total.Should().Be(subtotal - t.Discount);
    }

    [Fact]
    public void ExactlyOnThreshold_DoesNotEscalate()
    {
        // 100 is not strictly > 100, 500 is not strictly > 500
        OrderTotalsCalculator.Calculate(new[] { (1, 100m) }).DiscountRate.Should().Be(0m);
        OrderTotalsCalculator.Calculate(new[] { (1, 500m) }).DiscountRate.Should().Be(0.05m);
    }

    [Fact]
    public void Subtotal_SumsLines()
    {
        var t = OrderTotalsCalculator.Calculate(new[] { (3, 10m), (2, 20m), (1, 5m) });

        t.Subtotal.Should().Be(75m);
    }
}
