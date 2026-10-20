namespace OrdersApi.Domain;

public sealed class Order
{
    public const string CurrencyCode = "USD";

    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public long Id { get; private set; }
    public string IdempotencyKey { get; private set; } = "";

    /// <summary>SHA-256 of the normalised request body, used to tell a replay from key reuse.</summary>
    public string RequestHash { get; private set; } = "";

    public string CustomerRef { get; private set; } = "";
    public OrderStatus Status { get; private set; }
    public long SubtotalMinor { get; private set; }
    public int DiscountPercent { get; private set; }
    public long DiscountMinor { get; private set; }
    public long TotalMinor { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Concurrency token and ETag source. Starts at 1, bumped on every status change.</summary>
    public int Version { get; private set; }

    public IReadOnlyCollection<OrderLine> Lines => _lines;

    public static Order Place(string idempotencyKey, string requestHash, string customerRef,
        IEnumerable<OrderLine> lines, Quote quote, DateTimeOffset createdAt)
    {
        var order = new Order
        {
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CustomerRef = customerRef,
            Status = OrderStatus.New,
            SubtotalMinor = quote.SubtotalMinor,
            DiscountPercent = quote.DiscountPercent,
            DiscountMinor = quote.DiscountMinor,
            TotalMinor = quote.TotalMinor,
            // Always UTC: the database keeps UTC ticks, so the POST response must not carry a different offset.
            CreatedAt = createdAt.ToUniversalTime(),
            Version = 1,
        };
        order._lines.AddRange(lines);
        return order;
    }

    public void TransitionTo(OrderStatus target)
    {
        if (!OrderStatusRules.CanTransition(Status, target))
        {
            throw new DomainException("invalid-transition",
                $"Cannot move an order from {OrderStatusRules.ToWire(Status)} to {OrderStatusRules.ToWire(target)}.");
        }

        Status = target;
        Version++;
    }
}

public sealed class OrderLine
{
    private OrderLine()
    {
    }

    public long Id { get; private set; }
    public long OrderId { get; private set; }
    public string Sku { get; private set; } = "";
    public int Quantity { get; private set; }
    public long UnitPriceMinor { get; private set; }
    public long LineTotalMinor { get; private set; }

    public static OrderLine Create(string sku, int quantity, long unitPriceMinor) => new()
    {
        Sku = sku,
        Quantity = quantity,
        UnitPriceMinor = unitPriceMinor,
        LineTotalMinor = PricingPolicy.LineTotal(unitPriceMinor, quantity),
    };
}
