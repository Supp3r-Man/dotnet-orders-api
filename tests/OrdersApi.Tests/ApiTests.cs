using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OrdersApi.Tests;

public class ItemsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_returns_201_with_a_normalised_item()
    {
        var sku = "wid-" + Http.Unique().ToLowerInvariant();
        var response = await _client.PostAsJsonAsync("/items", new { sku = $" {sku} ", name = " Widget ", unitPriceMinor = 250, stock = 7 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/items/{sku.ToUpperInvariant()}", response.Headers.Location?.ToString());
        var body = await response.JsonBodyAsync();
        Assert.Equal(sku.ToUpperInvariant(), body.GetProperty("sku").GetString());
        Assert.Equal("Widget", body.GetProperty("name").GetString());
        Assert.Equal(250, body.GetProperty("unitPriceMinor").GetInt64());
        Assert.Equal(7, body.GetProperty("stock").GetInt32());
        Assert.Equal(1, body.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Get_is_case_insensitive_on_the_sku()
    {
        var sku = await _client.SeedItemAsync();
        var response = await _client.GetAsync($"/items/{sku.ToLowerInvariant()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sku, (await response.JsonBodyAsync()).GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Unknown_sku_is_a_404_problem() =>
        await (await _client.GetAsync("/items/NOPE-" + Http.Unique())).AssertProblemAsync(HttpStatusCode.NotFound, "item-not-found");

    [Fact]
    public async Task Duplicate_sku_is_a_409_even_with_different_case()
    {
        var sku = await _client.SeedItemAsync();
        var response = await _client.PostAsJsonAsync("/items", new { sku = sku.ToLowerInvariant(), name = "again", unitPriceMinor = 1, stock = 1 });
        await response.AssertProblemAsync(HttpStatusCode.Conflict, "sku-exists");
    }

    [Fact]
    public async Task Invalid_item_has_field_level_errors()
    {
        var response = await _client.PostAsJsonAsync("/items", new { sku = "bad sku", name = "", unitPriceMinor = 0, stock = -1 });
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal(
            [("sku", "invalid-format"), ("name", "required"), ("unitPriceMinor", "out-of-range"), ("stock", "out-of-range")],
            problem.FieldErrors());
    }

    [Fact]
    public async Task Simultaneous_creates_of_one_sku_give_one_winner()
    {
        var sku = "RACE-" + Http.Unique();
        var results = await Race.RunAsync(Enumerable.Range(0, 6).Select(_ => (Func<Task<HttpResponseMessage>>)
            (() => _client.PostAsJsonAsync("/items", new { sku, name = "r", unitPriceMinor = 5, stock = 1 }))).ToArray());

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }
}

public class OrderCreateTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_returns_201_with_totals_location_and_etag()
    {
        var sku = await _client.SeedItemAsync(price: 2_500, stock: 10);
        var response = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 3));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonBodyAsync();
        var id = body.GetProperty("id").GetInt64();
        Assert.Equal($"/orders/{id}", response.Headers.Location?.ToString());
        Assert.Equal("\"1\"", response.ETagOf());
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("alice", body.GetProperty("customerRef").GetString());
        Assert.Equal("NEW", body.GetProperty("status").GetString());
        Assert.Equal("USD", body.GetProperty("currency").GetString());
        Assert.Equal(7_500, body.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(0, body.GetProperty("discountPercent").GetInt32());
        Assert.Equal(0, body.GetProperty("discountMinor").GetInt64());
        Assert.Equal(7_500, body.GetProperty("totalMinor").GetInt64());
        var line = body.GetProperty("lines")[0];
        Assert.Equal(sku, line.GetProperty("sku").GetString());
        Assert.Equal(3, line.GetProperty("quantity").GetInt32());
        Assert.Equal(2_500, line.GetProperty("unitPriceMinor").GetInt64());
        Assert.Equal(7_500, line.GetProperty("lineTotalMinor").GetInt64());
        Assert.Equal(7, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Discount_tier_is_applied_with_half_up_rounding()
    {
        var sku = await _client.SeedItemAsync(price: 5_005, stock: 10);
        var body = await (await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 2))).JsonBodyAsync();

        Assert.Equal(10_010, body.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(5, body.GetProperty("discountPercent").GetInt32());
        Assert.Equal(501, body.GetProperty("discountMinor").GetInt64());
        Assert.Equal(9_509, body.GetProperty("totalMinor").GetInt64());
    }

    [Fact]
    public async Task Exactly_at_a_tier_threshold_the_discount_applies()
    {
        var sku = await _client.SeedItemAsync(price: 10_000, stock: 10);
        var body = await (await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1))).JsonBodyAsync();
        Assert.Equal(5, body.GetProperty("discountPercent").GetInt32());
        Assert.Equal(9_500, body.GetProperty("totalMinor").GetInt64());
    }

    [Fact]
    public async Task Replay_with_the_same_key_and_body_returns_the_stored_order_with_200()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        var first = await _client.PlaceAsync(key, "alice", (sku, 2));
        var replay = await _client.PlaceAsync(key, "alice", (sku, 2));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.ETagOf(), replay.ETagOf());
        Assert.Equal((await first.JsonBodyAsync()).ToString(), (await replay.JsonBodyAsync()).ToString());
        Assert.Equal(3, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Replay_treats_a_reordered_and_differently_cased_body_as_the_same_request()
    {
        var a = await _client.SeedItemAsync(stock: 5);
        var b = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        var first = await _client.PlaceAsync(key, "alice", (a, 1), (b, 2));
        var replay = await _client.PlaceAsync(key, "  alice ", (b.ToLowerInvariant(), 1), (a.ToLowerInvariant(), 1), (b, 1));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(4, await _client.StockAsync(a));
        Assert.Equal(3, await _client.StockAsync(b));
    }

    [Fact]
    public async Task Replay_after_a_status_change_returns_the_current_state_and_etag()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        var first = await _client.PlaceAsync(key, "alice", (sku, 1));
        var id = (await first.JsonBodyAsync()).GetProperty("id").GetInt64();
        await _client.ChangeStatusAsync(id, "PAID", first.ETagOf());

        var replay = await _client.PlaceAsync(key, "alice", (sku, 1));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("PAID", (await replay.JsonBodyAsync()).GetProperty("status").GetString());
        Assert.Equal("\"2\"", replay.ETagOf());
    }

    [Fact]
    public async Task Same_key_with_a_different_quantity_is_422_and_changes_nothing()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        await _client.PlaceAsync(key, "alice", (sku, 1));
        var response = await _client.PlaceAsync(key, "alice", (sku, 2));

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency-key-reuse");
        Assert.Equal(4, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Same_key_with_a_different_customer_is_422()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        await _client.PlaceAsync(key, "alice", (sku, 1));
        await (await _client.PlaceAsync(key, "bob", (sku, 1))).AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency-key-reuse");
    }

    [Fact]
    public async Task Different_keys_create_different_orders()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var one = await (await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1))).JsonBodyAsync();
        var two = await (await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1))).JsonBodyAsync();
        Assert.NotEqual(one.GetProperty("id").GetInt64(), two.GetProperty("id").GetInt64());
        Assert.Equal(3, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Missing_idempotency_key_is_a_400_with_a_field_error_in_the_standard_shape()
    {
        var sku = await _client.SeedItemAsync();
        var response = await _client.PlaceAsync(null, "alice", (sku, 1));

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("Idempotency-Key", "required")], problem.FieldErrors());
    }

    [Fact]
    public async Task Two_idempotency_key_headers_are_rejected_instead_of_merged_into_one_key()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent($$"""{"customerRef":"alice","lines":[{"sku":"{{sku}}","quantity":1}]}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", ["first", "second"]);

        var problem = await (await _client.SendAsync(request)).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("Idempotency-Key", "multiple-values")], problem.FieldErrors());
        Assert.Equal(5, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Missing_key_and_bad_body_report_every_field_at_once()
    {
        var response = await _client.PlaceRawAsync(null, """{"customerRef":"","lines":[{"sku":"A","quantity":0}]}""");
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal(
            [("Idempotency-Key", "required"), ("customerRef", "required"), ("lines[0].quantity", "out-of-range")],
            problem.FieldErrors());
    }

    [Fact]
    public async Task A_key_of_128_characters_is_accepted_and_129_is_rejected()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var ok = await _client.PlaceAsync(new string('a', 120) + Http.Unique()[..8], "alice", (sku, 1));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var tooLong = await _client.PlaceAsync(new string('a', 129), "alice", (sku, 1));
        var problem = await tooLong.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("Idempotency-Key", "too-long")], problem.FieldErrors());
    }

    [Fact]
    public async Task Unknown_sku_is_422_with_the_line_path_and_does_not_consume_the_key()
    {
        var good = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        var response = await _client.PlaceAsync(key, "alice", (good, 1), ("MISSING-" + Http.Unique(), 1));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "unknown-sku");
        // lines are sorted by SKU, so the unknown one (MISSING-...) is first or second depending on the SKU text
        Assert.Single(problem.FieldErrors());
        Assert.Equal("unknown-sku", problem.FieldErrors()[0].Code);
        Assert.Matches(@"^lines\[[01]\]\.sku$", problem.FieldErrors()[0].Field);
        Assert.Equal(5, await _client.StockAsync(good));

        var retry = await _client.PlaceAsync(key, "alice", (good, 1));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Insufficient_stock_is_422_and_does_not_consume_the_key()
    {
        var sku = await _client.SeedItemAsync(stock: 2);
        var key = "key-" + Http.Unique();
        var response = await _client.PlaceAsync(key, "alice", (sku, 3));

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient-stock");
        Assert.Equal(2, await _client.StockAsync(sku));
        Assert.Equal(HttpStatusCode.Created, (await _client.PlaceAsync(key, "alice", (sku, 2))).StatusCode);
        Assert.Equal(0, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task A_multi_line_order_is_all_or_nothing()
    {
        var plenty = await _client.SeedItemAsync(stock: 10);
        var scarce = await _client.SeedItemAsync(stock: 1);
        var response = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (plenty, 2), (scarce, 2));

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient-stock");
        Assert.Equal(10, await _client.StockAsync(plenty));
        Assert.Equal(1, await _client.StockAsync(scarce));
    }

    [Fact]
    public async Task Duplicate_sku_lines_are_merged_into_one_line()
    {
        var sku = await _client.SeedItemAsync(price: 100, stock: 10);
        var body = await (await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 2), (sku.ToLowerInvariant(), 3))).JsonBodyAsync();

        var lines = body.GetProperty("lines");
        Assert.Equal(1, lines.GetArrayLength());
        Assert.Equal(5, lines[0].GetProperty("quantity").GetInt32());
        Assert.Equal(500, lines[0].GetProperty("lineTotalMinor").GetInt64());
        Assert.Equal(5, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Buying_the_last_unit_is_allowed()
    {
        var sku = await _client.SeedItemAsync(stock: 1);
        Assert.Equal(HttpStatusCode.Created, (await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1))).StatusCode);
        Assert.Equal(0, await _client.StockAsync(sku));
    }
}

public class OrderReadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(long Id, HttpResponseMessage Created)> NewOrderAsync()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
        return ((await created.JsonBodyAsync()).GetProperty("id").GetInt64(), created);
    }

    [Fact]
    public async Task Get_returns_exactly_what_the_post_returned()
    {
        var (id, created) = await NewOrderAsync();
        var get = await _client.GetAsync($"/orders/{id}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.ETagOf(), get.ETagOf());
        Assert.Equal((await created.JsonBodyAsync()).ToString(), (await get.JsonBodyAsync()).ToString());
    }

    [Fact]
    public async Task Unknown_order_is_a_404_problem() =>
        await (await _client.GetAsync("/orders/999999")).AssertProblemAsync(HttpStatusCode.NotFound, "order-not-found");

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999")]
    public async Task A_bad_id_is_a_400_with_a_field_error(string id)
    {
        var problem = await (await _client.GetAsync($"/orders/{id}")).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("id", "invalid-value")], problem.FieldErrors());
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("W/\"1\"")]
    [InlineData("*")]
    [InlineData("\"7\", \"1\"")]
    public async Task If_none_match_with_the_current_etag_is_304_without_a_body(string header)
    {
        var (id, _) = await NewOrderAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", header);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("\"1\"", response.ETagOf());
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task If_none_match_with_an_old_etag_returns_the_order()
    {
        var (id, _) = await NewOrderAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_malformed_if_none_match_is_a_400()
    {
        var (id, _) = await NewOrderAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", "oops");
        var problem = await (await _client.SendAsync(request)).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("If-None-Match", "invalid-value")], problem.FieldErrors());
    }
}

public class OrderStatusApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(long Id, string Sku, string ETag)> NewOrderAsync(int quantity = 2, int stock = 10)
    {
        var sku = await _client.SeedItemAsync(stock: stock);
        var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, quantity));
        return ((await created.JsonBodyAsync()).GetProperty("id").GetInt64(), sku, created.ETagOf());
    }

    [Fact]
    public async Task Missing_if_match_is_428()
    {
        var (id, _, _) = await NewOrderAsync();
        await (await _client.ChangeStatusAsync(id, "PAID", null)).AssertProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
    }

    [Fact]
    public async Task Wrong_if_match_is_412_and_changes_nothing()
    {
        var (id, _, _) = await NewOrderAsync();
        await (await _client.ChangeStatusAsync(id, "PAID", "\"99\"")).AssertProblemAsync(HttpStatusCode.PreconditionFailed, "precondition-failed");
        var get = await _client.GetAsync($"/orders/{id}");
        Assert.Equal("NEW", (await get.JsonBodyAsync()).GetProperty("status").GetString());
        Assert.Equal("\"1\"", get.ETagOf());
    }

    [Fact]
    public async Task Correct_if_match_changes_the_status_and_returns_a_new_etag()
    {
        var (id, _, etag) = await NewOrderAsync();
        var response = await _client.ChangeStatusAsync(id, "PAID", etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PAID", (await response.JsonBodyAsync()).GetProperty("status").GetString());
        Assert.Equal("\"2\"", response.ETagOf());
        Assert.Equal("\"2\"", (await _client.GetAsync($"/orders/{id}")).ETagOf());
    }

    [Fact]
    public async Task A_stale_etag_is_412_after_someone_else_changed_the_order()
    {
        var (id, _, etag) = await NewOrderAsync();
        Assert.Equal(HttpStatusCode.OK, (await _client.ChangeStatusAsync(id, "PAID", etag)).StatusCode);
        await (await _client.ChangeStatusAsync(id, "SHIPPED", etag)).AssertProblemAsync(HttpStatusCode.PreconditionFailed, "precondition-failed");
    }

    [Fact]
    public async Task Wildcard_if_match_always_matches()
    {
        var (id, _, _) = await NewOrderAsync();
        Assert.Equal(HttpStatusCode.OK, (await _client.ChangeStatusAsync(id, "PAID", "*")).StatusCode);
    }

    [Fact]
    public async Task A_weak_etag_never_satisfies_if_match()
    {
        var (id, _, _) = await NewOrderAsync();
        await (await _client.ChangeStatusAsync(id, "PAID", "W/\"1\"")).AssertProblemAsync(HttpStatusCode.PreconditionFailed, "precondition-failed");
    }

    [Fact]
    public async Task A_malformed_if_match_is_a_400_field_error()
    {
        var (id, _, _) = await NewOrderAsync();
        var problem = await (await _client.ChangeStatusAsync(id, "PAID", "garbage")).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("If-Match", "invalid-value")], problem.FieldErrors());
    }

    [Fact]
    public async Task The_full_happy_path_bumps_the_etag_each_time()
    {
        var (id, _, etag) = await NewOrderAsync();
        var paid = await _client.ChangeStatusAsync(id, "paid", etag);
        var shipped = await _client.ChangeStatusAsync(id, "SHIPPED", paid.ETagOf());

        Assert.Equal("SHIPPED", (await shipped.JsonBodyAsync()).GetProperty("status").GetString());
        Assert.Equal("\"3\"", shipped.ETagOf());
    }

    [Theory]
    [InlineData("NEW")]
    [InlineData("SHIPPED")]
    public async Task Illegal_transitions_from_new_are_409(string to)
    {
        var (id, _, etag) = await NewOrderAsync();
        await (await _client.ChangeStatusAsync(id, to, etag)).AssertProblemAsync(HttpStatusCode.Conflict, "invalid-transition");
    }

    [Fact]
    public async Task Paid_cannot_go_back_to_new()
    {
        var (id, _, etag) = await NewOrderAsync();
        var paid = await _client.ChangeStatusAsync(id, "PAID", etag);
        await (await _client.ChangeStatusAsync(id, "NEW", paid.ETagOf())).AssertProblemAsync(HttpStatusCode.Conflict, "invalid-transition");
    }

    [Theory]
    [InlineData("PAID")]
    [InlineData("CANCELLED")]
    [InlineData("SHIPPED")]
    public async Task Shipped_is_terminal(string target)
    {
        var (id, _, etag) = await NewOrderAsync();
        var paid = await _client.ChangeStatusAsync(id, "PAID", etag);
        var shipped = await _client.ChangeStatusAsync(id, "SHIPPED", paid.ETagOf());
        await (await _client.ChangeStatusAsync(id, target, shipped.ETagOf())).AssertProblemAsync(HttpStatusCode.Conflict, "invalid-transition");
    }

    [Theory]
    [InlineData("PAID")]
    [InlineData("CANCELLED")]
    public async Task Cancelled_is_terminal(string target)
    {
        var (id, _, etag) = await NewOrderAsync();
        var cancelled = await _client.ChangeStatusAsync(id, "CANCELLED", etag);
        await (await _client.ChangeStatusAsync(id, target, cancelled.ETagOf())).AssertProblemAsync(HttpStatusCode.Conflict, "invalid-transition");
    }

    [Fact]
    public async Task Cancelling_a_new_order_restocks()
    {
        var (id, sku, etag) = await NewOrderAsync(quantity: 3, stock: 10);
        Assert.Equal(7, await _client.StockAsync(sku));
        Assert.Equal(HttpStatusCode.OK, (await _client.ChangeStatusAsync(id, "CANCELLED", etag)).StatusCode);
        Assert.Equal(10, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Cancelling_a_paid_order_restocks()
    {
        var (id, sku, etag) = await NewOrderAsync(quantity: 4, stock: 10);
        var paid = await _client.ChangeStatusAsync(id, "PAID", etag);
        Assert.Equal(6, await _client.StockAsync(sku));
        await _client.ChangeStatusAsync(id, "CANCELLED", paid.ETagOf());
        Assert.Equal(10, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Cancelling_restocks_every_line()
    {
        var a = await _client.SeedItemAsync(stock: 10);
        var b = await _client.SeedItemAsync(stock: 10);
        var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (a, 2), (b, 5));
        var id = (await created.JsonBodyAsync()).GetProperty("id").GetInt64();
        await _client.ChangeStatusAsync(id, "CANCELLED", created.ETagOf());
        Assert.Equal(10, await _client.StockAsync(a));
        Assert.Equal(10, await _client.StockAsync(b));
    }

    [Fact]
    public async Task Shipping_does_not_restock()
    {
        var (id, sku, etag) = await NewOrderAsync(quantity: 3, stock: 10);
        var paid = await _client.ChangeStatusAsync(id, "PAID", etag);
        await _client.ChangeStatusAsync(id, "SHIPPED", paid.ETagOf());
        Assert.Equal(7, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task A_failed_transition_does_not_restock()
    {
        var (id, sku, etag) = await NewOrderAsync(quantity: 3, stock: 10);
        var cancelled = await _client.ChangeStatusAsync(id, "CANCELLED", etag);
        await _client.ChangeStatusAsync(id, "CANCELLED", cancelled.ETagOf());
        Assert.Equal(10, await _client.StockAsync(sku));
    }

    [Theory]
    [InlineData("REFUNDED", "invalid-value")]
    [InlineData("", "required")]
    [InlineData(null, "required")]
    public async Task A_bad_status_value_is_a_400_field_error(string? status, string code)
    {
        var (id, _, etag) = await NewOrderAsync();
        var problem = await (await _client.ChangeStatusAsync(id, status, etag)).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("status", code)], problem.FieldErrors());
    }

    [Fact]
    public async Task An_empty_body_is_a_400()
    {
        var (id, _, etag) = await NewOrderAsync();
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/orders/{id}/status") { Content = new StringContent("", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        var problem = await (await _client.SendAsync(request)).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("body", "required")], problem.FieldErrors());
    }

    [Fact]
    public async Task Unknown_order_with_if_match_is_404_and_without_it_is_428()
    {
        await (await _client.ChangeStatusAsync(999_999, "PAID", "\"1\"")).AssertProblemAsync(HttpStatusCode.NotFound, "order-not-found");
        await (await _client.ChangeStatusAsync(999_999, "PAID", null)).AssertProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
    }

    [Fact]
    public async Task A_non_numeric_id_is_a_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/orders/abc/status")
        {
            Content = JsonContent.Create(new { status = "PAID" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        await (await _client.SendAsync(request)).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
    }
}

public class OrderListingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<long> PlaceAtAsync(DateTimeOffset when, string customer, string sku)
    {
        factory.Time.Set(when);
        var response = await _client.PlaceAsync("key-" + Http.Unique(), customer, (sku, 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonBodyAsync()).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> ListAsync(string query)
    {
        var response = await _client.GetAsync("/orders?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonBodyAsync();
    }

    private static long[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("id").GetInt64()).ToArray();

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Newest_first_by_created_at()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 50);
        var oldest = await PlaceAtAsync(T0, customer, sku);
        var newest = await PlaceAtAsync(T0.AddMinutes(10), customer, sku);
        var middle = await PlaceAtAsync(T0.AddMinutes(5), customer, sku);

        Assert.Equal([newest, middle, oldest], Ids(await ListAsync($"customerRef={customer}")));
    }

    [Fact]
    public async Task Orders_created_at_the_same_instant_are_ordered_by_id_descending()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 50);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await PlaceAtAsync(T0, customer, sku));
        }

        ids.Reverse();
        Assert.Equal(ids, Ids(await ListAsync($"customerRef={customer}")));
    }

    [Fact]
    public async Task Paging_through_ties_returns_every_order_exactly_once()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 50);
        var created = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            created.Add(await PlaceAtAsync(T0, customer, sku));
        }

        var seen = new List<long>();
        for (var page = 0; page < 3; page++)
        {
            var body = await ListAsync($"customerRef={customer}&size=3&page={page}");
            Assert.Equal(7, body.GetProperty("totalCount").GetInt64());
            Assert.Equal(3, body.GetProperty("totalPages").GetInt32());
            Assert.Equal(page, body.GetProperty("page").GetInt32());
            Assert.Equal(3, body.GetProperty("size").GetInt32());
            seen.AddRange(Ids(body));
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(created.OrderByDescending(x => x), seen);
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_keeps_the_total()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 5);
        await PlaceAtAsync(T0, customer, sku);
        var body = await ListAsync($"customerRef={customer}&page=5&size=10");
        Assert.Empty(Ids(body));
        Assert.Equal(1, body.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, body.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task Filters_by_status()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 10);
        var keep = await PlaceAtAsync(T0, customer, sku);
        var other = await PlaceAtAsync(T0, customer, sku);
        var paid = await _client.ChangeStatusAsync(other, "PAID", "*");
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        Assert.Equal([keep], Ids(await ListAsync($"customerRef={customer}&status=new")));
        Assert.Equal([other], Ids(await ListAsync($"customerRef={customer}&status=PAID")));
        Assert.Empty(Ids(await ListAsync($"customerRef={customer}&status=SHIPPED")));
    }

    [Fact]
    public async Task Filters_by_customer()
    {
        var one = "c-" + Http.Unique();
        var two = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 10);
        var a = await PlaceAtAsync(T0, one, sku);
        await PlaceAtAsync(T0, two, sku);
        Assert.Equal([a], Ids(await ListAsync($"customerRef={one}")));
    }

    [Fact]
    public async Task Created_after_is_exclusive()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 10);
        await PlaceAtAsync(T0, customer, sku);
        var later = await PlaceAtAsync(T0.AddSeconds(1), customer, sku);

        var cutoff = Uri.EscapeDataString(T0.ToString("o"));
        Assert.Equal([later], Ids(await ListAsync($"customerRef={customer}&createdAfter={cutoff}")));
    }

    [Fact]
    public async Task Created_after_works_with_a_non_utc_offset()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 10);
        await PlaceAtAsync(T0, customer, sku);
        var later = await PlaceAtAsync(T0.AddHours(2), customer, sku);

        // T0 + 1h expressed at +05:00
        var cutoff = Uri.EscapeDataString(T0.AddHours(1).ToOffset(TimeSpan.FromHours(5)).ToString("o"));
        Assert.Equal([later], Ids(await ListAsync($"customerRef={customer}&createdAfter={cutoff}")));
    }

    [Fact]
    public async Task Size_100_is_the_cap_and_101_is_rejected()
    {
        Assert.Equal(100, (await ListAsync("size=100")).GetProperty("size").GetInt32());
        var problem = await (await _client.GetAsync("/orders?size=101")).AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("size", "out-of-range")], problem.FieldErrors());
    }

    [Fact]
    public async Task The_default_page_size_is_20()
    {
        var body = await ListAsync("");
        Assert.Equal(20, body.GetProperty("size").GetInt32());
        Assert.Equal(0, body.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Bad_query_values_are_field_errors()
    {
        var problem = await (await _client.GetAsync("/orders?status=nope&page=-1&size=x&createdAfter=yesterday"))
            .AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal(
            [("status", "invalid-value"), ("createdAfter", "invalid-value"), ("page", "out-of-range"), ("size", "invalid-value")],
            problem.FieldErrors());
    }
}
