using OrdersApi.Domain;

namespace OrdersApi.Application;

/// <summary>Raw input as deserialised from the request body. Everything is nullable on purpose: the validator reports it.</summary>
public sealed record CreateOrderInput(string? CustomerRef, IReadOnlyList<OrderLineInput?>? Lines);

public sealed record OrderLineInput(string? Sku, int? Quantity);

public sealed record CreateItemInput(string? Sku, string? Name, long? UnitPriceMinor, int? Stock);

/// <summary>A validated order request: trimmed customer, upper-cased SKUs, duplicate SKUs summed, sorted by SKU.</summary>
public sealed record NormalizedOrder(string IdempotencyKey, string CustomerRef, IReadOnlyList<NormalizedLine> Lines);

public sealed record NormalizedLine(string Sku, int Quantity);

public sealed record Placement(Order Order, bool Replayed);

public sealed record OrderListQuery(OrderStatus? Status, string? CustomerRef, DateTimeOffset? CreatedAfter, int Page, int Size);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, long TotalCount)
{
    public int TotalPages => (int)((TotalCount + Size - 1) / Size);
}
