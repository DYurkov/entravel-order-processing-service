using FluentValidation;
using OrderService.Api.Endpoints;
using OrderService.Api.Infrastructure;

namespace OrderService.Api.Validators;

public class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    private const decimal TotalTolerance = 0.01m;

    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.CustomerId)
            .NotEqual(Guid.Empty).WithMessage("CustomerId must not be empty.");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Order must contain at least one item.")
            .Must(items => items.Count <= 100).WithMessage("Order cannot contain more than 100 items.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Sku).NotEmpty().WithMessage("SKU is required.");
            item.RuleFor(i => i.Quantity).GreaterThanOrEqualTo(1).WithMessage("Quantity must be at least 1.");
            item.RuleFor(i => i.UnitPrice).GreaterThanOrEqualTo(0).WithMessage("UnitPrice must be non-negative.");
        });

        RuleFor(x => x.TotalAmount)
            .GreaterThanOrEqualTo(0).WithMessage("TotalAmount must be non-negative.")
            .Must((req, total) =>
            {
                if (req.Items is null || req.Items.Count == 0) return true;
                var subtotal = OrderTotalsCalculator
                    .Calculate(req.Items.Select(i => (i.Quantity, i.UnitPrice)))
                    .Subtotal;
                return Math.Abs(total - subtotal) <= TotalTolerance;
            })
            .WithMessage("TotalAmount must match sum of quantity * unitPrice.");
    }
}
