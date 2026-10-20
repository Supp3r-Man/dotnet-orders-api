namespace OrdersApi.Domain;

public enum OrderStatus
{
    New,
    Paid,
    Shipped,
    Cancelled,
}

/// <summary>The order state machine and the wire names of the statuses (NEW, PAID, SHIPPED, CANCELLED).</summary>
public static class OrderStatusRules
{
    // NEW -> PAID | CANCELLED, PAID -> SHIPPED | CANCELLED. SHIPPED and CANCELLED are terminal.
    public static bool CanTransition(OrderStatus from, OrderStatus to) => from switch
    {
        OrderStatus.New => to is OrderStatus.Paid or OrderStatus.Cancelled,
        OrderStatus.Paid => to is OrderStatus.Shipped or OrderStatus.Cancelled,
        _ => false,
    };

    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Shipped or OrderStatus.Cancelled;

    public static string ToWire(OrderStatus status) => status.ToString().ToUpperInvariant();

    /// <summary>Case-insensitive, names only: "paid" and "PAID" parse, "1" and "" do not.</summary>
    public static bool TryParse(string? text, out OrderStatus status)
    {
        foreach (var candidate in Enum.GetValues<OrderStatus>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }
}
