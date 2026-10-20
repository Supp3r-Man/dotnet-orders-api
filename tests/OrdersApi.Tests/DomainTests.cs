using OrdersApi.Domain;

namespace OrdersApi.Tests;

public class PricingPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(9_999, 0)]
    [InlineData(10_000, 5)]
    [InlineData(49_999, 5)]
    [InlineData(50_000, 10)]
    [InlineData(99_999, 10)]
    [InlineData(100_000, 15)]
    [InlineData(5_000_000, 15)]
    public void Tier_thresholds_are_inclusive(long subtotal, int expectedPercent) =>
        Assert.Equal(expectedPercent, PricingPolicy.Default.PercentFor(subtotal));

    [Theory]
    [InlineData(10_010, 5, 501)] // 500.5 rounds UP (HALF_EVEN would give 500)
    [InlineData(10_009, 5, 500)] // 500.45 rounds down
    [InlineData(10_011, 5, 501)] // 500.55 rounds up
    [InlineData(10_030, 5, 502)] // 501.5 rounds up to 502
    [InlineData(10_000, 5, 500)] // exact
    [InlineData(0, 0, 0)]
    [InlineData(1, 5, 0)] // 0.05 -> 0
    [InlineData(10, 5, 1)] // 0.5 -> 1 (half up)
    [InlineData(100_010, 15, 15_002)] // 15001.5 -> 15002
    public void Discount_rounds_half_up_to_a_minor_unit(long subtotal, int percent, long expected) =>
        Assert.Equal(expected, PricingPolicy.DiscountFor(subtotal, percent));

    [Fact]
    public void Quote_sums_lines_applies_the_tier_and_subtracts_the_discount()
    {
        var lines = new[] { OrderLine.Create("A", 3, 2_500), OrderLine.Create("B", 1, 2_505) };
        var quote = PricingPolicy.Default.Quote(lines);

        Assert.Equal(10_005, quote.SubtotalMinor);
        Assert.Equal(5, quote.DiscountPercent);
        Assert.Equal(500, quote.DiscountMinor); // 500.25 -> 500
        Assert.Equal(9_505, quote.TotalMinor);
    }

    [Fact]
    public void Quote_below_the_first_tier_has_no_discount()
    {
        var quote = PricingPolicy.Default.Quote([OrderLine.Create("A", 1, 9_999)]);
        Assert.Equal(new Quote(9_999, 0, 0, 9_999), quote);
    }

    [Fact]
    public void Quote_at_the_top_tier()
    {
        var quote = PricingPolicy.Default.Quote([OrderLine.Create("A", 10, 10_001)]);
        Assert.Equal(100_010, quote.SubtotalMinor);
        Assert.Equal(15, quote.DiscountPercent);
        Assert.Equal(15_002, quote.DiscountMinor);
        Assert.Equal(85_008, quote.TotalMinor);
    }

    [Fact]
    public void Line_total_is_unit_price_times_quantity() =>
        Assert.Equal(7_500, PricingPolicy.LineTotal(2_500, 3));

    [Fact]
    public void Line_total_overflow_is_an_error_not_a_wrong_number()
    {
        var ex = Assert.Throws<DomainException>(() => PricingPolicy.LineTotal(long.MaxValue, 2));
        Assert.Equal("amount-too-large", ex.Code);
    }

    [Fact]
    public void Discount_overflow_is_an_error()
    {
        var ex = Assert.Throws<DomainException>(() => PricingPolicy.DiscountFor(long.MaxValue, 15));
        Assert.Equal("amount-too-large", ex.Code);
    }

    [Fact]
    public void Subtotal_overflow_is_an_error()
    {
        var half = long.MaxValue / 2 + 1;
        var lines = new[] { OrderLine.Create("A", 1, half), OrderLine.Create("B", 1, half) };
        var ex = Assert.Throws<DomainException>(() => PricingPolicy.Default.Quote(lines));
        Assert.Equal("amount-too-large", ex.Code);
    }

    [Fact]
    public void Tiers_are_sorted_internally_so_input_order_does_not_matter()
    {
        var policy = new PricingPolicy([new DiscountTier(1_000, 20), new DiscountTier(100, 2), new DiscountTier(500, 10)]);
        Assert.Equal(2, policy.PercentFor(100));
        Assert.Equal(10, policy.PercentFor(999));
        Assert.Equal(20, policy.PercentFor(1_000));
        Assert.Equal(0, policy.PercentFor(99));
    }
}

public class OrderStatusRulesTests
{
    public static TheoryData<OrderStatus, OrderStatus, bool> Matrix() => new()
    {
        { OrderStatus.New, OrderStatus.New, false },
        { OrderStatus.New, OrderStatus.Paid, true },
        { OrderStatus.New, OrderStatus.Shipped, false },
        { OrderStatus.New, OrderStatus.Cancelled, true },
        { OrderStatus.Paid, OrderStatus.New, false },
        { OrderStatus.Paid, OrderStatus.Paid, false },
        { OrderStatus.Paid, OrderStatus.Shipped, true },
        { OrderStatus.Paid, OrderStatus.Cancelled, true },
        { OrderStatus.Shipped, OrderStatus.New, false },
        { OrderStatus.Shipped, OrderStatus.Paid, false },
        { OrderStatus.Shipped, OrderStatus.Shipped, false },
        { OrderStatus.Shipped, OrderStatus.Cancelled, false },
        { OrderStatus.Cancelled, OrderStatus.New, false },
        { OrderStatus.Cancelled, OrderStatus.Paid, false },
        { OrderStatus.Cancelled, OrderStatus.Shipped, false },
        { OrderStatus.Cancelled, OrderStatus.Cancelled, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Transition_matrix(OrderStatus from, OrderStatus to, bool allowed) =>
        Assert.Equal(allowed, OrderStatusRules.CanTransition(from, to));

    [Theory]
    [InlineData(OrderStatus.New, false)]
    [InlineData(OrderStatus.Paid, false)]
    [InlineData(OrderStatus.Shipped, true)]
    [InlineData(OrderStatus.Cancelled, true)]
    public void Terminal_states(OrderStatus status, bool terminal) =>
        Assert.Equal(terminal, OrderStatusRules.IsTerminal(status));

    [Theory]
    [InlineData("NEW", true, OrderStatus.New)]
    [InlineData("paid", true, OrderStatus.Paid)]
    [InlineData("Shipped", true, OrderStatus.Shipped)]
    [InlineData("cancelled", true, OrderStatus.Cancelled)]
    [InlineData("1", false, default(OrderStatus))]
    [InlineData("", false, default(OrderStatus))]
    [InlineData(null, false, default(OrderStatus))]
    [InlineData("REFUNDED", false, default(OrderStatus))]
    [InlineData("PAID,SHIPPED", false, default(OrderStatus))]
    public void Parse_accepts_names_only(string? text, bool ok, OrderStatus expected)
    {
        Assert.Equal(ok, OrderStatusRules.TryParse(text, out var status));
        Assert.Equal(expected, status);
    }

    [Fact]
    public void Wire_names_are_upper_case() =>
        Assert.Equal(["NEW", "PAID", "SHIPPED", "CANCELLED"],
            Enum.GetValues<OrderStatus>().Select(OrderStatusRules.ToWire));
}

public class ItemTests
{
    [Fact]
    public void Reserve_decrements_stock_and_bumps_the_version()
    {
        var item = Item.Create("A", "Alpha", 100, 5);
        item.Reserve(2);
        Assert.Equal(3, item.Stock);
        Assert.Equal(2, item.Version);
    }

    [Fact]
    public void Reserve_can_take_the_last_unit()
    {
        var item = Item.Create("A", "Alpha", 100, 1);
        item.Reserve(1);
        Assert.Equal(0, item.Stock);
    }

    [Fact]
    public void Reserve_beyond_stock_fails_and_changes_nothing()
    {
        var item = Item.Create("A", "Alpha", 100, 1);
        var ex = Assert.Throws<DomainException>(() => item.Reserve(2));
        Assert.Equal("insufficient-stock", ex.Code);
        Assert.Equal(1, item.Stock);
        Assert.Equal(1, item.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_requires_a_positive_quantity(int quantity)
    {
        var item = Item.Create("A", "Alpha", 100, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Reserve(quantity));
    }

    [Fact]
    public void Restock_adds_stock_and_bumps_the_version()
    {
        var item = Item.Create("A", "Alpha", 100, 1);
        item.Restock(4);
        Assert.Equal(5, item.Stock);
        Assert.Equal(2, item.Version);
    }

    [Fact]
    public void Restock_requires_a_positive_quantity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Item.Create("A", "Alpha", 100, 1).Restock(0));

    [Fact]
    public void Create_rejects_negative_stock_and_non_positive_price()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Item.Create("A", "Alpha", 100, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Item.Create("A", "Alpha", 0, 1));
    }
}

public class OrderTests
{
    private static Order NewOrder() => Order.Place("k", "h", "cust",
        [OrderLine.Create("A", 2, 500)], PricingPolicy.Default.Quote([OrderLine.Create("A", 2, 500)]),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_placed_order_is_new_at_version_one()
    {
        var order = NewOrder();
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(1, order.Version);
        Assert.Equal(1_000, order.TotalMinor);
        Assert.Single(order.Lines);
    }

    [Fact]
    public void Transition_changes_status_and_bumps_the_version()
    {
        var order = NewOrder();
        order.TransitionTo(OrderStatus.Paid);
        order.TransitionTo(OrderStatus.Shipped);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal(3, order.Version);
    }

    [Fact]
    public void Illegal_transition_throws_and_leaves_the_order_alone()
    {
        var order = NewOrder();
        var ex = Assert.Throws<DomainException>(() => order.TransitionTo(OrderStatus.Shipped));
        Assert.Equal("invalid-transition", ex.Code);
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(1, order.Version);
    }
}
