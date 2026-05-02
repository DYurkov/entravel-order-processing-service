using FluentValidation;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Api.Domain;
using OrderService.Api.Infrastructure;
using OrderService.Api.Persistence;
using OrderService.Contracts;

namespace OrderService.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orders", CreateOrderAsync)
            .WithName("CreateOrder")
            .Produces<CreateOrderResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .WithOpenApi();

        app.MapGet("/api/orders/{id:guid}", GetOrderAsync)
            .WithName("GetOrder")
            .Produces<OrderResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> CreateOrderAsync(
        CreateOrderRequest request,
        IValidator<CreateOrderRequest> validator,
        AppDbContext db,
        IPublishEndpoint publisher,
        HttpContext httpContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();

        if (idempotencyKey != null)
        {
            var existing = await db.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);
            if (existing != null)
            {
                return Results.Accepted(
                    $"/api/orders/{existing.Id}",
                    new CreateOrderResponse(existing.Id, existing.Status.ToString(), $"/api/orders/{existing.Id}"));
            }
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            Items = request.Items.Select(i => new OrderItem
            {
                Sku = i.Sku,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList(),
            TotalAmount = request.TotalAmount,
            Status = OrderStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        await publisher.Publish(new OrderSubmitted
        {
            OrderId = order.Id,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        OrderMetrics.OrdersReceived.WithLabels(request.CustomerId.ToString()).Inc();
        logger.LogInformation("Order {OrderId} received for customer {CustomerId}", order.Id, request.CustomerId);

        return Results.Accepted(
            $"/api/orders/{order.Id}",
            new CreateOrderResponse(order.Id, order.Status.ToString(), $"/api/orders/{order.Id}"));
    }

    private static async Task<IResult> GetOrderAsync(
        Guid id,
        AppDbContext db,
        CancellationToken ct)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return order == null
            ? Results.NotFound()
            : Results.Ok(OrderMappings.ToResponse(order));
    }
}
