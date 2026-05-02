namespace OrderService.Api.Infrastructure;

public class OrderRejectedException : Exception
{
    public OrderRejectedException(string message) : base(message) { }
}
