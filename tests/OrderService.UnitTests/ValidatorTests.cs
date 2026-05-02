using FluentAssertions;
using FluentValidation;
using FluentValidation.TestHelper;
using OrderService.Api.Endpoints;
using OrderService.Api.Validators;

namespace OrderService.UnitTests;

public class ValidatorTests
{
    private readonly CreateOrderRequestValidator _validator = new();

    [Fact]
    public void Valid_Request_Passes()
    {
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-001", 2, 15.50m)],
            31.00m);

        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyCustomerId_Fails()
    {
        var request = new CreateOrderRequest(
            Guid.Empty,
            [new OrderItemRequest("SKU-001", 1, 10m)],
            10m);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.CustomerId);
    }

    [Fact]
    public void EmptyItems_Fails()
    {
        var request = new CreateOrderRequest(Guid.NewGuid(), [], 0m);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void TooManyItems_Fails()
    {
        var items = Enumerable.Range(1, 101)
            .Select(i => new OrderItemRequest($"SKU-{i:000}", 1, 1m))
            .ToList();
        var request = new CreateOrderRequest(Guid.NewGuid(), items, 101m);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void ZeroQuantity_Fails()
    {
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-001", 0, 10m)],
            0m);

        var result = _validator.TestValidate(request);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void NegativeUnitPrice_Fails()
    {
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-001", 1, -5m)],
            -5m);

        var result = _validator.TestValidate(request);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void TotalAmountMismatch_Fails()
    {
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-001", 2, 15.50m)],
            999m); // Should be 31.00

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TotalAmount);
    }

    [Fact]
    public void TotalAmountWithinTolerance_Passes()
    {
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-001", 2, 15.50m)],
            31.005m); // within 0.01 of 31.00

        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.TotalAmount);
    }
}
