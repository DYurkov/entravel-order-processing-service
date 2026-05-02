namespace OrderService.Api.Domain;

public class OrderItem
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
