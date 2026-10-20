namespace OrdersApi.Domain;

/// <summary>A sellable SKU with stock. <see cref="Version"/> is the EF Core concurrency token.</summary>
public sealed class Item
{
    private Item()
    {
    }

    public long Id { get; private set; }
    public string Sku { get; private set; } = "";
    public string Name { get; private set; } = "";
    public long UnitPriceMinor { get; private set; }
    public int Stock { get; private set; }

    /// <summary>
    /// Integer concurrency token (SQLite has no rowversion). Every stock change bumps it, and EF Core adds
    /// "WHERE Version = @original" to the UPDATE, so a writer holding a stale copy affects 0 rows and fails.
    /// </summary>
    public int Version { get; private set; }

    public static Item Create(string sku, string name, long unitPriceMinor, int stock)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitPriceMinor);
        return new Item { Sku = sku, Name = name, UnitPriceMinor = unitPriceMinor, Stock = stock, Version = 1 };
    }

    public void Reserve(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (quantity > Stock)
        {
            throw new DomainException("insufficient-stock",
                $"SKU {Sku}: requested {quantity}, available {Stock}.");
        }

        Stock -= quantity;
        Version++;
    }

    public void Restock(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        Stock += quantity;
        Version++;
    }
}
