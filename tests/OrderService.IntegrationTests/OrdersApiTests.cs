using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Api.Domain;
using OrderService.Api.Endpoints;
using OrderService.Api.Persistence;

namespace OrderService.IntegrationTests;

public class OrdersApiTests : IClassFixture<CustomWebAppFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebAppFactory _factory;

    public OrdersApiTests(CustomWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CreateOrderRequest ValidOrder(string sku = "SKU-001", int qty = 1) =>
        new(Guid.NewGuid(), [new OrderItemRequest(sku, qty, 15.50m)], 15.50m);

    [Fact]
    public async Task PostOrder_ReturnsAccepted()
    {
        var response = await _client.PostAsJsonAsync("/api/orders", ValidOrder());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("Pending");
        body.TrackingUrl.Should().Contain(body.OrderId.ToString());
    }

    [Fact]
    public async Task PostOrder_PersistsToDatabase()
    {
        var customerId = Guid.NewGuid();
        var response = await _client.PostAsJsonAsync("/api/orders",
            new CreateOrderRequest(customerId, [new OrderItemRequest("SKU-001", 1, 15.50m)], 15.50m));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.FindAsync(body!.OrderId);

        // status is racy here, the consumer may already have flipped it
        order.Should().NotBeNull();
        order!.CustomerId.Should().Be(customerId);
    }

    [Fact]
    public async Task PostOrder_EventuallyProcessed()
    {
        var response = await _client.PostAsJsonAsync("/api/orders", ValidOrder("SKU-003", 1));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
        var orderId = body!.OrderId;

        var processed = await WaitForStatusAsync(orderId, "Processed", TimeSpan.FromSeconds(20));
        processed.Should().NotBeNull();
        processed!.Status.Should().Be("Processed");
        processed.Discount.Should().Be(0);
    }

    [Fact]
    public async Task PostOrder_OverDiscountThreshold_AppliesDiscount()
    {
        // SKU-005 is $500/unit, 2 of them = $1000 → 10% discount tier
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-005", 2, 500m)],
            1000m);

        var response = await _client.PostAsJsonAsync("/api/orders", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
        var processed = await WaitForStatusAsync(body!.OrderId, "Processed", TimeSpan.FromSeconds(20));

        processed.Should().NotBeNull();
        processed!.Status.Should().Be("Processed");
        processed.TotalAmount.Should().Be(1000m);
        processed.Discount.Should().Be(100m);
    }

    [Fact]
    public async Task PostOrder_IdempotencyKey_DeduplicatesRequest()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var request = ValidOrder("SKU-003", 1);

        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
            { Content = JsonContent.Create(request) };
        req1.Headers.Add("Idempotency-Key", idempotencyKey);

        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
            { Content = JsonContent.Create(request) };
        req2.Headers.Add("Idempotency-Key", idempotencyKey);

        var response1 = await _client.SendAsync(req1);
        var response2 = await _client.SendAsync(req2);

        response1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response2.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body1 = await response1.Content.ReadFromJsonAsync<CreateOrderResponse>();
        var body2 = await response2.Content.ReadFromJsonAsync<CreateOrderResponse>();

        body1!.OrderId.Should().Be(body2!.OrderId);
    }

    [Fact]
    public async Task PostOrder_UnknownSku_Rejected()
    {
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-DOES-NOT-EXIST", 1, 10m)],
            10m);

        var response = await _client.PostAsJsonAsync("/api/orders", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
        var orderId = body!.OrderId;

        var rejected = await WaitForStatusAsync(orderId, "Rejected", TimeSpan.FromSeconds(20));
        rejected.Should().NotBeNull();
        rejected!.Status.Should().Be("Rejected");
        rejected.FailureReason.Should().Contain("SKU-DOES-NOT-EXIST");
    }

    [Fact]
    public async Task PostOrder_InsufficientStock_Rejected()
    {
        // SKU-005 is seeded with stock=10
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            [new OrderItemRequest("SKU-005", 99, 500m)],
            49500m);

        var response = await _client.PostAsJsonAsync("/api/orders", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
        var orderId = body!.OrderId;

        var rejected = await WaitForStatusAsync(orderId, "Rejected", TimeSpan.FromSeconds(20));
        rejected.Should().NotBeNull();
        rejected!.Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task GetOrder_NotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/orders/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostOrder_InvalidRequest_Returns400()
    {
        var request = new CreateOrderRequest(Guid.Empty, [], -5m);
        var response = await _client.PostAsJsonAsync("/api/orders", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HealthCheck_Liveness_Returns200()
    {
        var response = await _client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<OrderResponse?> WaitForStatusAsync(
        Guid orderId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        OrderResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/orders/{orderId}");
            if (response.IsSuccessStatusCode)
            {
                last = await response.Content.ReadFromJsonAsync<OrderResponse>();
                if (last?.Status == expectedStatus) return last;
            }
            await Task.Delay(500);
        }
        return last;
    }
}
