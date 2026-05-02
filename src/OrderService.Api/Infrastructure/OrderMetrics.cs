using Prometheus;

namespace OrderService.Api.Infrastructure;

public static class OrderMetrics
{
    public static readonly Counter OrdersReceived = Metrics
        .CreateCounter(
            "orders_received_total",
            "Total number of orders received",
            new CounterConfiguration { LabelNames = ["customer"] });

    public static readonly Counter OrdersProcessed = Metrics
        .CreateCounter(
            "orders_processed_total",
            "Total number of orders processed by final status",
            new CounterConfiguration { LabelNames = ["status"] });

    public static readonly Histogram ProcessingDuration = Metrics
        .CreateHistogram(
            "order_processing_duration_seconds",
            "Order processing duration in seconds",
            new HistogramConfiguration
            {
                LabelNames = ["status"],
                Buckets = [0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0]
            });
}
