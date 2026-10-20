using OrdersApi.Application;
using OrdersApi.Domain;

namespace OrdersApi.Tests;

public class RequestFingerprintTests
{
    private static string Hash(string customer, params (string Sku, int Qty)[] lines) =>
        RequestFingerprint.Compute(Validation.ValidateOrder("key",
            new CreateOrderInput(customer, lines.Select(l => (OrderLineInput?)new OrderLineInput(l.Sku, l.Qty)).ToList())));

    [Fact]
    public void Hash_is_64_lowercase_hex_characters()
    {
        var hash = Hash("alice", ("A", 1));
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Same_request_hashes_the_same() =>
        Assert.Equal(Hash("alice", ("A", 1), ("B", 2)), Hash("alice", ("A", 1), ("B", 2)));

    [Fact]
    public void Line_order_does_not_matter() =>
        Assert.Equal(Hash("alice", ("A", 1), ("B", 2)), Hash("alice", ("B", 2), ("A", 1)));

    [Fact]
    public void Sku_case_and_padding_do_not_matter() =>
        Assert.Equal(Hash("alice", ("abc", 1)), Hash("alice", ("  ABC ", 1)));

    [Fact]
    public void Customer_padding_does_not_matter() =>
        Assert.Equal(Hash("alice", ("A", 1)), Hash("  alice  ", ("A", 1)));

    [Fact]
    public void Split_quantities_for_one_sku_equal_the_merged_line() =>
        Assert.Equal(Hash("alice", ("A", 3)), Hash("alice", ("A", 1), ("a", 2)));

    [Fact]
    public void A_different_customer_changes_the_hash() =>
        Assert.NotEqual(Hash("alice", ("A", 1)), Hash("bob", ("A", 1)));

    [Fact]
    public void Customer_case_is_significant() =>
        Assert.NotEqual(Hash("alice", ("A", 1)), Hash("Alice", ("A", 1)));

    [Fact]
    public void A_different_quantity_changes_the_hash() =>
        Assert.NotEqual(Hash("alice", ("A", 1)), Hash("alice", ("A", 2)));

    [Fact]
    public void A_different_sku_changes_the_hash() =>
        Assert.NotEqual(Hash("alice", ("A", 1)), Hash("alice", ("B", 1)));

    [Fact]
    public void An_extra_line_changes_the_hash() =>
        Assert.NotEqual(Hash("alice", ("A", 1)), Hash("alice", ("A", 1), ("B", 1)));

    [Fact]
    public void Length_prefix_stops_sku_boundary_collisions() =>
        Assert.NotEqual(Hash("alice", ("AB", 1), ("C", 1)), Hash("alice", ("A", 1), ("BC", 1)));

    [Fact]
    public void Length_prefix_stops_customer_sku_collisions()
    {
        // Without prefixes both canonical strings would read "customer=ab|sku=c..." versus "customer=a|sku=bc...".
        Assert.NotEqual(Hash("ab", ("C", 1)), Hash("a", ("BC", 1)));
    }

    [Fact]
    public void Canonical_form_is_pinned_by_a_golden_hash()
    {
        // Pins the exact canonical string: changing it would make every stored key look like a mismatch.
        Assert.Equal("3253f97b8eab34675d12193be99bdc0326925eb739a644d6f7af4fbef2858c23", Hash("alice", ("A", 1), ("B", 2)));
    }
}

public class ValidationTests
{
    private static AppException Fails(string? key, CreateOrderInput? input) =>
        Assert.Throws<AppException>(() => Validation.ValidateOrder(key, input));

    private static CreateOrderInput Order(string? customer = "alice", params (string? Sku, int? Qty)[] lines) =>
        new(customer, lines.Select(l => (OrderLineInput?)new OrderLineInput(l.Sku, l.Qty)).ToList());

    private static CreateOrderInput OneLine => Order("alice", ("A", 1));

    [Fact]
    public void A_valid_request_is_normalised()
    {
        var result = Validation.ValidateOrder("key-1", Order("  alice ", ("b", 2), (" a ", 1), ("B", 3)));
        Assert.Equal("alice", result.CustomerRef);
        Assert.Equal("key-1", result.IdempotencyKey);
        Assert.Equal([new NormalizedLine("A", 1), new NormalizedLine("B", 5)], result.Lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_key_is_a_field_error_on_the_header(string? key)
    {
        var ex = Fails(key, OneLine);
        Assert.Equal(ErrorKind.Validation, ex.Kind);
        Assert.Equal("validation-failed", ex.Code);
        Assert.Equal([new FieldError("Idempotency-Key", "required", ex.Errors[0].Message)], ex.Errors);
    }

    [Fact]
    public void Key_of_exactly_128_characters_is_accepted() =>
        Assert.Equal(new string('k', 128), Validation.ValidateOrder(new string('k', 128), OneLine).IdempotencyKey);

    [Fact]
    public void Key_of_129_characters_is_too_long()
    {
        var ex = Fails(new string('k', 129), OneLine);
        Assert.Equal(("Idempotency-Key", "too-long"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\tkey")]
    [InlineData("café")]
    public void Key_must_be_printable_ascii_without_spaces(string key)
    {
        var ex = Fails(key, OneLine);
        Assert.Equal(("Idempotency-Key", "invalid-format"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void Missing_body_is_reported_next_to_a_missing_key()
    {
        var ex = Fails(null, null);
        Assert.Equal(["Idempotency-Key", "body"], ex.Errors.Select(e => e.Field));
    }

    [Fact]
    public void All_problems_are_collected_in_one_error()
    {
        var ex = Fails(null, Order(" ", ("bad sku", 0), (null, null)));
        Assert.Equal(
            ["Idempotency-Key:required", "customerRef:required", "lines[0].sku:invalid-format",
             "lines[0].quantity:out-of-range", "lines[1].sku:required", "lines[1].quantity:required"],
            ex.Errors.Select(e => $"{e.Field}:{e.Code}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Customer_is_required(string? customer) =>
        Assert.Equal(("customerRef", "required"), (Fails("k", Order(customer, ("A", 1))).Errors[0].Field,
            Fails("k", Order(customer, ("A", 1))).Errors[0].Code));

    [Fact]
    public void Customer_of_64_characters_is_accepted_and_65_is_not()
    {
        Assert.Equal(64, Validation.ValidateOrder("k", Order(new string('c', 64), ("A", 1))).CustomerRef.Length);
        var ex = Fails("k", Order(new string('c', 65), ("A", 1)));
        Assert.Equal(("customerRef", "too-long"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void Customer_length_is_measured_after_trimming() =>
        Assert.Equal(64, Validation.ValidateOrder("k", Order(" " + new string('c', 64) + " ", ("A", 1))).CustomerRef.Length);

    [Fact]
    public void Lines_are_required()
    {
        var ex = Fails("k", new CreateOrderInput("alice", null));
        Assert.Equal(("lines", "required"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void An_empty_line_list_is_rejected()
    {
        var ex = Fails("k", new CreateOrderInput("alice", []));
        Assert.Equal(("lines", "required"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void Fifty_lines_are_accepted_and_fifty_one_are_not()
    {
        CreateOrderInput Many(int n) =>
            new("alice", Enumerable.Range(0, n).Select(i => (OrderLineInput?)new OrderLineInput($"S{i}", 1)).ToList());

        Assert.Equal(50, Validation.ValidateOrder("k", Many(50)).Lines.Count);
        var ex = Fails("k", Many(51));
        Assert.Equal(("lines", "too-many"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void A_null_line_is_reported_with_its_index()
    {
        var ex = Fails("k", new CreateOrderInput("alice", [new OrderLineInput("A", 1), null]));
        Assert.Equal(("lines[1]", "required"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1001)]
    public void Quantity_must_be_between_1_and_1000(int quantity)
    {
        var ex = Fails("k", Order("alice", ("A", quantity)));
        Assert.Equal(("lines[0].quantity", "out-of-range"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Quantity_bounds_are_inclusive(int quantity) =>
        Assert.Equal(quantity, Validation.ValidateOrder("k", Order("alice", ("A", quantity))).Lines[0].Quantity);

    [Fact]
    public void Merged_quantity_for_one_sku_is_capped_too()
    {
        var ex = Fails("k", Order("alice", ("A", 600), ("a", 401)));
        Assert.Equal(("lines[0].quantity", "out-of-range"), (ex.Errors[0].Field, ex.Errors[0].Code));
        Assert.Equal(1000, Validation.ValidateOrder("k", Order("alice", ("A", 600), ("a", 400))).Lines[0].Quantity);
    }

    [Theory]
    [InlineData("has space", "invalid-format")]
    [InlineData("semi;colon", "invalid-format")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData(null, "required")]
    public void Sku_rules(string? sku, string code)
    {
        var ex = Fails("k", Order("alice", (sku, 1)));
        Assert.Equal(("lines[0].sku", code), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void Sku_of_64_characters_is_accepted_and_65_is_not()
    {
        Assert.Equal(64, Validation.ValidateOrder("k", Order("alice", (new string('s', 64), 1))).Lines[0].Sku.Length);
        var ex = Fails("k", Order("alice", (new string('s', 65), 1)));
        Assert.Equal(("lines[0].sku", "too-long"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Fact]
    public void Missing_quantity_is_required()
    {
        var ex = Fails("k", Order("alice", ("A", null)));
        Assert.Equal(("lines[0].quantity", "required"), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    // ---- items

    [Fact]
    public void A_valid_item_is_normalised()
    {
        var item = Validation.ValidateItem(new CreateItemInput(" widget-1 ", " Widget ", 250, 3));
        Assert.Equal(new CreateItemInput("WIDGET-1", "Widget", 250, 3), item);
    }

    [Fact]
    public void Item_errors_are_collected()
    {
        var ex = Assert.Throws<AppException>(() => Validation.ValidateItem(new CreateItemInput("bad sku", "", 0, -1)));
        Assert.Equal(
            ["sku:invalid-format", "name:required", "unitPriceMinor:out-of-range", "stock:out-of-range"],
            ex.Errors.Select(e => $"{e.Field}:{e.Code}"));
    }

    [Fact]
    public void Item_missing_fields_are_required()
    {
        var ex = Assert.Throws<AppException>(() => Validation.ValidateItem(new CreateItemInput(null, null, null, null)));
        Assert.Equal(
            ["sku:required", "name:required", "unitPriceMinor:required", "stock:required"],
            ex.Errors.Select(e => $"{e.Field}:{e.Code}"));
    }

    [Fact]
    public void Item_bounds()
    {
        Assert.Equal(0, Validation.ValidateItem(new CreateItemInput("A", "n", 1, 0)).Stock);
        Assert.Equal(Validation.MaxUnitPriceMinor,
            Validation.ValidateItem(new CreateItemInput("A", "n", Validation.MaxUnitPriceMinor, Validation.MaxStock)).UnitPriceMinor);
        Assert.Throws<AppException>(() => Validation.ValidateItem(new CreateItemInput("A", "n", Validation.MaxUnitPriceMinor + 1, 1)));
        Assert.Throws<AppException>(() => Validation.ValidateItem(new CreateItemInput("A", "n", 1, Validation.MaxStock + 1)));
        Assert.Throws<AppException>(() => Validation.ValidateItem(new CreateItemInput("A", new string('n', 101), 1, 1)));
        Assert.Equal(100, Validation.ValidateItem(new CreateItemInput("A", new string('n', 100), 1, 1)).Name!.Length);
    }

    [Fact]
    public void Item_body_is_required() =>
        Assert.Equal("body", Assert.Throws<AppException>(() => Validation.ValidateItem(null)).Errors[0].Field);

    // ---- status

    [Theory]
    [InlineData(" paid ", OrderStatus.Paid)]
    [InlineData("CANCELLED", OrderStatus.Cancelled)]
    public void Status_is_parsed(string text, OrderStatus expected) =>
        Assert.Equal(expected, Validation.ValidateStatus(text));

    [Theory]
    [InlineData(null, "required")]
    [InlineData("  ", "required")]
    [InlineData("REFUNDED", "invalid-value")]
    [InlineData("2", "invalid-value")]
    public void Bad_status_is_a_field_error(string? text, string code)
    {
        var ex = Assert.Throws<AppException>(() => Validation.ValidateStatus(text));
        Assert.Equal(("status", code), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    // ---- list query

    [Fact]
    public void List_query_defaults()
    {
        var q = Validation.ValidateListQuery(null, null, null, null, null);
        Assert.Equal(new OrderListQuery(null, null, null, 0, 20), q);
    }

    [Fact]
    public void List_query_parses_every_filter()
    {
        var q = Validation.ValidateListQuery("paid", " alice ", "2026-01-15T12:00:00Z", "2", "50");
        Assert.Equal(OrderStatus.Paid, q.Status);
        Assert.Equal("alice", q.CustomerRef);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), q.CreatedAfter);
        Assert.Equal((2, 50), (q.Page, q.Size));
    }

    [Fact]
    public void A_timestamp_without_an_offset_is_taken_as_utc()
    {
        var q = Validation.ValidateListQuery(null, null, "2026-01-15T12:00:00", null, null);
        Assert.Equal(TimeSpan.Zero, q.CreatedAfter!.Value.Offset);
        Assert.Equal(12, q.CreatedAfter.Value.Hour);
    }

    [Theory]
    [InlineData("size", "0", "out-of-range")]
    [InlineData("size", "101", "out-of-range")]
    [InlineData("size", "-1", "out-of-range")]
    [InlineData("size", "abc", "invalid-value")]
    [InlineData("size", "1.5", "invalid-value")]
    [InlineData("page", "-1", "out-of-range")]
    [InlineData("page", "100001", "out-of-range")]
    [InlineData("page", "x", "invalid-value")]
    public void Paging_parameters_are_range_checked(string name, string value, string code)
    {
        var ex = Assert.Throws<AppException>(() => Validation.ValidateListQuery(
            null, null, null, name == "page" ? value : null, name == "size" ? value : null));
        Assert.Equal((name, code), (ex.Errors[0].Field, ex.Errors[0].Code));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    public void Size_bounds_are_inclusive(string size, int expected) =>
        Assert.Equal(expected, Validation.ValidateListQuery(null, null, null, null, size).Size);

    [Fact]
    public void List_query_collects_all_bad_parameters()
    {
        var ex = Assert.Throws<AppException>(() => Validation.ValidateListQuery("nope", new string('c', 65), "yesterday", "-1", "0"));
        Assert.Equal(
            ["status:invalid-value", "customerRef:too-long", "createdAfter:invalid-value", "page:out-of-range", "size:out-of-range"],
            ex.Errors.Select(e => $"{e.Field}:{e.Code}"));
    }

    [Fact]
    public void Blank_filters_are_ignored()
    {
        var q = Validation.ValidateListQuery(" ", " ", " ", " ", " ");
        Assert.Equal(new OrderListQuery(null, null, null, 0, 20), q);
    }
}

public class PreconditionTests
{
    [Fact]
    public void Etag_is_the_quoted_version() => Assert.Equal("\"7\"", ETags.ForVersion(7));

    private static EntityTagPrecondition Parse(string text)
    {
        Assert.True(EntityTagPrecondition.TryParse(text, out var result));
        return result!;
    }

    [Theory]
    [InlineData("\"3\"", true)]
    [InlineData("\"2\", \"3\"", true)]
    [InlineData("\"2\",\"3\"", true)]
    [InlineData("*", true)]
    [InlineData("\"2\"", false)]
    [InlineData("W/\"3\"", false)]
    public void If_match_uses_strong_comparison(string header, bool matches) =>
        Assert.Equal(matches, Parse(header).MatchesStrong("\"3\""));

    [Theory]
    [InlineData("\"3\"", true)]
    [InlineData("W/\"3\"", true)]
    [InlineData("*", true)]
    [InlineData("\"4\"", false)]
    public void If_none_match_uses_weak_comparison(string header, bool matches) =>
        Assert.Equal(matches, Parse(header).MatchesWeak("\"3\""));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]
    [InlineData("\"3")]
    [InlineData("W/3")]
    [InlineData("\"3\"x")]
    [InlineData("*x")]
    [InlineData(",,")]
    public void Malformed_values_are_rejected(string header)
    {
        Assert.False(EntityTagPrecondition.TryParse(header, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Null_is_rejected() => Assert.False(EntityTagPrecondition.TryParse(null, out _));
}
