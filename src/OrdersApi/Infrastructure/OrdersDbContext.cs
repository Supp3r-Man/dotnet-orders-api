using Microsoft.EntityFrameworkCore;
using OrdersApi.Domain;

namespace OrdersApi.Infrastructure;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(item =>
        {
            item.HasKey(i => i.Id);
            item.Property(i => i.Sku).HasMaxLength(64).IsRequired();
            item.HasIndex(i => i.Sku).IsUnique();
            item.Property(i => i.Name).HasMaxLength(100).IsRequired();
            // Optimistic concurrency: EF adds "AND Version = @original" to every UPDATE of an item.
            item.Property(i => i.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.HasKey(o => o.Id);
            order.Property(o => o.IdempotencyKey).HasMaxLength(128).IsRequired();
            // The race backstop: two requests with the same key can both pass the lookup, only one can insert.
            order.HasIndex(o => o.IdempotencyKey).IsUnique();
            order.Property(o => o.RequestHash).HasMaxLength(64).IsRequired();
            order.Property(o => o.CustomerRef).HasMaxLength(64).IsRequired();
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
            // SQLite stores DateTimeOffset as text and cannot ORDER BY it. UTC ticks (100 ns) in an INTEGER sort
            // correctly and round-trip every digit that TimeProvider produced.
            order.Property(o => o.CreatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            order.Property(o => o.Version).IsConcurrencyToken();
            order.HasIndex(o => new { o.CreatedAt, o.Id });
            order.HasIndex(o => o.Status);
            order.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            order.Navigation(o => o.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OrderLine>(line =>
        {
            line.HasKey(l => l.Id);
            line.Property(l => l.Sku).HasMaxLength(64).IsRequired();
        });
    }
}
