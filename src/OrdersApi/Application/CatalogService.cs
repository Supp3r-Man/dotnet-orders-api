using Microsoft.EntityFrameworkCore;
using OrdersApi.Domain;
using OrdersApi.Infrastructure;

namespace OrdersApi.Application;

public sealed class CatalogService(OrdersDbContext db)
{
    public async Task<Item> CreateAsync(CreateItemInput? input, CancellationToken ct)
    {
        var valid = Validation.ValidateItem(input);
        if (await db.Items.AnyAsync(i => i.Sku == valid.Sku, ct))
        {
            throw SkuExists(valid.Sku!);
        }

        var item = Item.Create(valid.Sku!, valid.Name!, valid.UnitPriceMinor!.Value, valid.Stock!.Value);
        db.Items.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
        {
            // Lost a race with another request creating the same SKU; the unique index is the backstop.
            throw SkuExists(valid.Sku!);
        }

        return item;
    }

    public async Task<Item> GetAsync(string sku, CancellationToken ct)
    {
        var normalized = Validation.NormalizeSku(sku);
        return await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Sku == normalized, ct)
               ?? throw AppException.NotFound("item-not-found", $"No item with SKU {normalized}.");
    }

    private static AppException SkuExists(string sku) =>
        new(ErrorKind.Conflict, "sku-exists", $"An item with SKU {sku} already exists.");
}
