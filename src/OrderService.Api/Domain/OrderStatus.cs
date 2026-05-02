namespace OrderService.Api.Domain;

public enum OrderStatus
{
    Pending,
    Processing,
    Processed,
    Failed,
    Rejected
}
