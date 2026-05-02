using Microsoft.EntityFrameworkCore;
using OrderService.Api.Domain;

namespace OrderService.Api.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<Inventory> Inventory { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(o => o.Id);
            b.Property(o => o.Id).HasColumnName("id");
            b.Property(o => o.CustomerId).HasColumnName("customer_id");
            b.Property(o => o.TotalAmount).HasColumnName("total_amount").HasColumnType("numeric(18,2)");
            b.Property(o => o.Status).HasColumnName("status").HasConversion<string>();
            b.Property(o => o.Discount).HasColumnName("discount").HasColumnType("numeric(18,2)");
            b.Property(o => o.CreatedAt).HasColumnName("created_at");
            b.Property(o => o.ProcessedAt).HasColumnName("processed_at");
            b.Property(o => o.FailureReason).HasColumnName("failure_reason");
            b.Property(o => o.IdempotencyKey).HasColumnName("idempotency_key");
            b.HasIndex(o => o.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");

            b.OwnsMany(o => o.Items, items =>
            {
                items.ToTable("order_items");
                items.WithOwner().HasForeignKey("order_id");
                items.HasKey("Id");
                items.Property<int>("Id").HasColumnName("id").ValueGeneratedOnAdd();
                items.Property(i => i.Sku).HasColumnName("sku").IsRequired();
                items.Property(i => i.Quantity).HasColumnName("quantity");
                items.Property(i => i.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(18,2)");
            });
        });

        modelBuilder.Entity<Inventory>(b =>
        {
            b.ToTable("inventory");
            b.HasKey(i => i.Sku);
            b.Property(i => i.Sku).HasColumnName("sku");
            b.Property(i => i.Name).HasColumnName("name").IsRequired();
            b.Property(i => i.Price).HasColumnName("price").HasColumnType("numeric(18,2)");
            b.Property(i => i.Stock).HasColumnName("stock");

            // xmin instead of UseXminAsConcurrencyToken (deprecated in Npgsql 8)
            b.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });
    }
}
