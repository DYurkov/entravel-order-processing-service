using System.Net.Sockets;
using FluentValidation;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using Serilog;
using Serilog.Events;
using OrderService.Api.Domain;
using OrderService.Api.Endpoints;
using OrderService.Api.Persistence;
using OrderService.Api.Validators;
using OrderService.Api.Workers;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("MassTransit", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter()));

var connStr = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Database=orderservice;Username=postgres;Password=postgres";

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connStr));

var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "amqp://guest:guest@localhost:5672";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderConsumer, OrderConsumerDefinition>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(new Uri(rabbitHost));
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>();

// AspNetCore.HealthChecks.RabbitMQ has an ABI clash with the RabbitMQ.Client
// version MassTransit 8.3 needs, so we just probe the AMQP port directly.
builder.Services.AddHealthChecks()
    .AddNpgSql(connStr, name: "postgres", tags: ["ready"])
    .AddCheck("rabbitmq", () =>
    {
        try
        {
            var uri = new Uri(rabbitHost);
            using var client = new TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(2))
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("RabbitMQ TCP connect timed out");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }, tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseHttpMetrics();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapOrderEndpoints();

app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapMetrics("/metrics");

await InitialiseDatabaseAsync(app);

app.Run();

static async Task InitialiseDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup");

    for (var attempt = 1; attempt <= 8; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            await SeedInventoryAsync(db);
            return;
        }
        catch (Exception ex) when (attempt < 8)
        {
            logger.LogWarning(ex, "Postgres not ready, retrying in 3s (attempt {Attempt}/8)", attempt);
            await Task.Delay(3_000);
        }
    }
}

static async Task SeedInventoryAsync(AppDbContext db)
{
    if (await db.Inventory.AnyAsync()) return;

    db.Inventory.AddRange(
        new Inventory { Sku = "SKU-001", Name = "Widget A", Price = 15.50m, Stock = 100 },
        new Inventory { Sku = "SKU-002", Name = "Widget B", Price = 100.00m, Stock = 50 },
        new Inventory { Sku = "SKU-003", Name = "Widget C", Price = 25.00m, Stock = 200 },
        new Inventory { Sku = "SKU-004", Name = "Widget D", Price = 75.00m, Stock = 30 },
        new Inventory { Sku = "SKU-005", Name = "Widget E", Price = 500.00m, Stock = 10 });

    await db.SaveChangesAsync();
}

public partial class Program { }
