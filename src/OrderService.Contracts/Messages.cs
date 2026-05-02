namespace OrderService.Contracts;

public record OrderSubmitted
{
    public Guid OrderId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
