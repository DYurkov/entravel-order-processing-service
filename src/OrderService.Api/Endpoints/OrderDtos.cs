using OrderService.Api.Domain;

namespace OrderService.Api.Endpoints;

public record CreateOrderRequest(
    Guid CustomerId,
    List<OrderItemRequest> Items,
    decimal TotalAmount);

public record OrderItemRequest(string Sku, int Quantity, decimal UnitPrice);

public record CreateOrderResponse(Guid OrderId, string Status, string TrackingUrl);

public record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string Status,
    decimal TotalAmount,
    decimal Discount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    string? FailureReason,
    List<OrderItemResponse> Items);

public record OrderItemResponse(string Sku, int Quantity, decimal UnitPrice);

public static class OrderMappings
{
    public static OrderResponse ToResponse(Order order) => new(
        order.Id,
        order.CustomerId,
        order.Status.ToString(),
        order.TotalAmount,
        order.Discount,
        order.CreatedAt,
        order.ProcessedAt,
        order.FailureReason,
        order.Items.Select(i => new OrderItemResponse(i.Sku, i.Quantity, i.UnitPrice)).ToList());
}
