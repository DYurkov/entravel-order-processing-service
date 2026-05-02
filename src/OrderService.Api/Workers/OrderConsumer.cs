using System.Diagnostics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Api.Domain;
using OrderService.Api.Infrastructure;
using OrderService.Api.Persistence;
using OrderService.Contracts;

namespace OrderService.Api.Workers;

public class OrderConsumer : IConsumer<OrderSubmitted>
{
    private const int MaxAttempts = 5;

    private readonly AppDbContext _db;
    private readonly ILogger<OrderConsumer> _logger;

    public OrderConsumer(AppDbContext db, ILogger<OrderConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        var sw = Stopwatch.StartNew();
        var orderId = context.Message.OrderId;
        var ct = context.CancellationToken;

        var order = await ClaimOrderAsync(orderId, ct);
        if (order is null) return;

        string outcome;
        try
        {
            await ProcessOrderAsync(order, ct);
            outcome = "processed";
            _logger.LogInformation("Order {OrderId} processed", orderId);
        }
        catch (OrderRejectedException ex)
        {
            await FinaliseAsync(order, OrderStatus.Rejected, ex.Message, ct);
            outcome = "rejected";
            _logger.LogWarning("Order {OrderId} rejected: {Reason}", orderId, ex.Message);
        }
        catch (Exception ex)
        {
            var attempt = context.GetRetryAttempt() + 1;
            _logger.LogError(ex, "Error processing order {OrderId} (attempt {Attempt})", orderId, attempt);

            // The change tracker still holds the dirty inventory rows that lost
            // the xmin race, so any further SaveChanges through this context
            // would re-throw. Drop them before the corrective write.
            _db.ChangeTracker.Clear();

            if (attempt >= MaxAttempts)
            {
                await MarkFailedAsync(orderId, ex.Message, ct);
                outcome = "failed";
            }
            else
            {
                // unclaim so the next retry can pick it up again
                await ResetToPendingAsync(orderId, ct);
                throw;
            }
        }

        OrderMetrics.OrdersProcessed.WithLabels(outcome).Inc();
        OrderMetrics.ProcessingDuration.WithLabels(outcome).Observe(sw.Elapsed.TotalSeconds);
    }

    private async Task<Order?> ClaimOrderAsync(Guid orderId, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found, skipping", orderId);
            return null;
        }

        if (order.Status != OrderStatus.Pending)
        {
            _logger.LogInformation("Order {OrderId} already in {Status}, skipping", orderId, order.Status);
            return null;
        }

        order.Status = OrderStatus.Processing;
        await _db.SaveChangesAsync(ct);
        return order;
    }

    private async Task ProcessOrderAsync(Order order, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var skus = order.Items.Select(i => i.Sku).Distinct().ToArray();
        var inventory = await _db.Inventory
            .Where(i => skus.Contains(i.Sku))
            .ToDictionaryAsync(i => i.Sku, ct);

        foreach (var item in order.Items)
        {
            if (!inventory.TryGetValue(item.Sku, out var inv))
                throw new OrderRejectedException($"SKU '{item.Sku}' not found in inventory.");
            if (inv.Stock < item.Quantity)
                throw new OrderRejectedException(
                    $"Insufficient stock for SKU '{item.Sku}': requested {item.Quantity}, available {inv.Stock}.");
        }

        foreach (var item in order.Items)
        {
            var inv = inventory[item.Sku];
            item.UnitPrice = inv.Price;
            inv.Stock -= item.Quantity;
        }

        var totals = OrderTotalsCalculator.Calculate(
            order.Items.Select(i => (i.Quantity, i.UnitPrice)));

        order.TotalAmount = totals.Subtotal;
        order.Discount = totals.Discount;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task FinaliseAsync(Order order, OrderStatus status, string reason, CancellationToken ct)
    {
        order.Status = status;
        order.FailureReason = reason;
        order.ProcessedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkFailedAsync(Guid orderId, string reason, CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlAsync(
                $"UPDATE orders SET status = 'Failed', failure_reason = {reason}, processed_at = {DateTimeOffset.UtcNow} WHERE id = {orderId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark order {OrderId} as Failed", orderId);
        }
    }

    private async Task ResetToPendingAsync(Guid orderId, CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlAsync(
                $"UPDATE orders SET status = 'Pending' WHERE id = {orderId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset order {OrderId} to Pending", orderId);
        }
    }
}

public class OrderConsumerDefinition : ConsumerDefinition<OrderConsumer>
{
    public OrderConsumerDefinition()
    {
        EndpointName = "order-processing";
        ConcurrentMessageLimit = 10;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<OrderConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(1_000, 2_000, 5_000, 10_000, 20_000));
    }
}
