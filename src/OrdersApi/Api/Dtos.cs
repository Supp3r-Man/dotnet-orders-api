using OrdersApi.Application;
using OrdersApi.Domain;

namespace OrdersApi.Api;

public sealed record OrderLineResponse(string Sku, int Quantity, long UnitPriceMinor, long LineTotalMinor);

/// <summary>All money fields are integer minor units (cents) of <see cref="Currency"/>.</summary>
public sealed record OrderResponse(
    long Id,
    string CustomerRef,
    string Status,
    string Currency,
    long SubtotalMinor,
    int DiscountPercent,
    long DiscountMinor,
    long TotalMinor,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.CustomerRef,
        OrderStatusRules.ToWire(order.Status),
        Order.CurrencyCode,
        order.SubtotalMinor,
        order.DiscountPercent,
        order.DiscountMinor,
        order.TotalMinor,
        order.CreatedAt,
        order.Lines
            .OrderBy(l => l.Sku, StringComparer.Ordinal)
            .Select(l => new OrderLineResponse(l.Sku, l.Quantity, l.UnitPriceMinor, l.LineTotalMinor))
            .ToList());
}

public sealed record ItemResponse(string Sku, string Name, long UnitPriceMinor, int Stock, int Version)
{
    public static ItemResponse From(Item item) =>
        new(item.Sku, item.Name, item.UnitPriceMinor, item.Stock, item.Version);
}

public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int Size, long TotalCount, int TotalPages);

public sealed record ChangeStatusRequest(string? Status);
