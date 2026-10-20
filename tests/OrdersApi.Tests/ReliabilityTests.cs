using System.Net;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrdersApi.Api;
using OrdersApi.Application;
using OrdersApi.Domain;
using OrdersApi.Infrastructure;

namespace OrdersApi.Tests;

/// <summary>Releases N actions at the same instant from N dedicated threads (a real barrier, not just Task.WhenAll).</summary>
public static class Race
{
    public static async Task<HttpResponseMessage[]> RunAsync(params Func<Task<HttpResponseMessage>>[] actions)
    {
        using var barrier = new Barrier(actions.Length);
        var tasks = actions.Select(action => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return action();
        }, TaskCreationOptions.LongRunning).Unwrap()).ToArray();
        return await Task.WhenAll(tasks);
    }
}

/// <summary>Lets a test run code inside SaveChanges, after the service has read and before it writes.</summary>
public sealed class TestHook : SaveChangesInterceptor
{
    public Func<DbContext, Task>? OnSaving { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (OnSaving is { } hook)
        {
            await hook(eventData.Context!);
        }

        return result;
    }
}

public sealed class HookedApiFactory : ApiFactory
{
    public TestHook Hook { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<IInterceptor>(Hook);
}

/// <summary>Deterministic proofs of the error mappings: the test forces the race instead of hoping for it.</summary>
public class ForcedRaceTests(HookedApiFactory factory) : IClassFixture<HookedApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static bool IsOrderInsert(DbContext db) => db.ChangeTracker.Entries<Order>().Any(e => e.State == EntityState.Added);

    [Fact]
    public async Task Losing_the_unique_index_race_on_the_idempotency_key_is_a_409_and_changes_no_stock()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        var fired = 0;
        factory.Hook.OnSaving = async db =>
        {
            if (!IsOrderInsert(db) || Interlocked.Increment(ref fired) > 1)
            {
                return;
            }

            // another request commits an order with the same key between our lookup and our insert
            await using var other = factory.NewDb();
            var lines = new[] { OrderLine.Create(sku, 1, 1000) };
            other.Orders.Add(Order.Place(key, "some-other-hash", "other", lines, PricingPolicy.Default.Quote(lines), factory.Time.GetUtcNow()));
            await other.SaveChangesAsync();
        };

        try
        {
            var response = await _client.PlaceAsync(key, "alice", (sku, 1));
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "idempotency-key-conflict");
            Assert.Equal(5, await _client.StockAsync(sku));
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }

    [Fact]
    public async Task A_stock_conflict_is_retried_and_then_succeeds()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var fired = 0;
        factory.Hook.OnSaving = async db =>
        {
            if (!IsOrderInsert(db) || Interlocked.Increment(ref fired) > 1)
            {
                return;
            }

            await using var other = factory.NewDb();
            (await other.Items.SingleAsync(i => i.Sku == sku)).Reserve(1);
            await other.SaveChangesAsync();
        };

        try
        {
            var response = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(2, fired); // first save lost, second save won
            Assert.Equal(3, await _client.StockAsync(sku)); // 5 - 1 (other writer) - 1 (ours)
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }

    [Fact]
    public async Task A_stock_conflict_that_drains_the_stock_turns_into_insufficient_stock_on_retry()
    {
        var sku = await _client.SeedItemAsync(stock: 1);
        var fired = 0;
        factory.Hook.OnSaving = async db =>
        {
            if (!IsOrderInsert(db) || Interlocked.Increment(ref fired) > 1)
            {
                return;
            }

            await using var other = factory.NewDb();
            (await other.Items.SingleAsync(i => i.Sku == sku)).Reserve(1);
            await other.SaveChangesAsync();
        };

        try
        {
            var response = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
            await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient-stock");
            Assert.Equal(0, await _client.StockAsync(sku));
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }

    [Fact]
    public async Task A_key_race_lost_on_the_stock_row_replays_the_winner_on_retry()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        var fired = 0;
        factory.Hook.OnSaving = async db =>
        {
            if (!IsOrderInsert(db) || Interlocked.Increment(ref fired) > 1)
            {
                return;
            }

            // the "other" request is the same logical request: it fully commits first
            await using var other = factory.NewDb();
            var item = await other.Items.SingleAsync(i => i.Sku == sku);
            item.Reserve(1);
            var lines = new[] { OrderLine.Create(sku, 1, item.UnitPriceMinor) };
            var request = Validation.ValidateOrder(key, new CreateOrderInput("alice", [new OrderLineInput(sku, 1)]));
            other.Orders.Add(Order.Place(key, RequestFingerprint.Compute(request), "alice", lines,
                PricingPolicy.Default.Quote(lines), factory.Time.GetUtcNow()));
            await other.SaveChangesAsync();
        };

        try
        {
            var response = await _client.PlaceAsync(key, "alice", (sku, 1));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("true", response.Headers.GetValues("Idempotent-Replayed").Single());
            Assert.Equal(4, await _client.StockAsync(sku));
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }

    [Fact]
    public async Task Endless_conflicts_give_up_with_409_after_the_attempt_cap()
    {
        var sku = await _client.SeedItemAsync(stock: 100);
        var fired = 0;
        factory.Hook.OnSaving = async db =>
        {
            if (!IsOrderInsert(db))
            {
                return;
            }

            Interlocked.Increment(ref fired);
            await using var other = factory.NewDb();
            (await other.Items.SingleAsync(i => i.Sku == sku)).Restock(1);
            await other.SaveChangesAsync();
        };

        try
        {
            var response = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "concurrent-update");
            Assert.Equal(OrderService.MaxAttempts, fired);
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }

    [Fact]
    public async Task A_status_change_that_loses_the_token_race_is_answered_412()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
        var id = (await created.JsonBodyAsync()).GetProperty("id").GetInt64();
        var fired = 0;
        factory.Hook.OnSaving = async db =>
        {
            if (Interlocked.Increment(ref fired) > 1)
            {
                return;
            }

            // someone else cancels the order after we read it but before we write
            await using var other = factory.NewDb();
            var order = await other.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == id);
            order.TransitionTo(OrderStatus.Cancelled);
            await other.SaveChangesAsync();
        };

        try
        {
            var response = await _client.ChangeStatusAsync(id, "PAID", created.ETagOf());
            await response.AssertProblemAsync(HttpStatusCode.PreconditionFailed, "precondition-failed");
            var after = await _client.GetAsync($"/orders/{id}");
            Assert.Equal("CANCELLED", (await after.JsonBodyAsync()).GetProperty("status").GetString());
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_500_in_the_standard_shape_without_leaking_details()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        factory.Hook.OnSaving = db => IsOrderInsert(db)
            ? throw new InvalidOperationException("secret connection string and stack")
            : Task.CompletedTask;

        try
        {
            var response = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
            var problem = await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal-error");
            Assert.Equal("An unexpected error occurred.", problem.GetProperty("detail").GetString());
            Assert.DoesNotContain("secret", problem.ToString());
            Assert.Equal(5, await _client.StockAsync(sku));
        }
        finally
        {
            factory.Hook.OnSaving = null;
        }
    }
}

/// <summary>Real parallel requests released by a barrier. Each scenario is repeated because one lucky schedule proves little.</summary>
public class ConcurrencyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Two_buyers_race_for_the_last_unit_and_exactly_one_wins()
    {
        for (var round = 0; round < 15; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 1);
            var results = await Race.RunAsync(
                () => _client.PlaceAsync("a-" + Http.Unique(), "alice", (sku, 1)),
                () => _client.PlaceAsync("b-" + Http.Unique(), "bob", (sku, 1)));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            var loser = results.Single(r => r.StatusCode != HttpStatusCode.Created);
            Assert.Contains(loser.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity });
            Assert.Equal(0, await _client.StockAsync(sku));
            Assert.Equal(1, await CountOrdersForSkuAsync(sku));
        }
    }

    [Fact]
    public async Task Eight_buyers_race_for_the_last_unit_and_exactly_one_wins()
    {
        for (var round = 0; round < 5; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 1);
            var results = await Race.RunAsync(Enumerable.Range(0, 8)
                .Select(i => (Func<Task<HttpResponseMessage>>)(() => _client.PlaceAsync($"k{i}-" + Http.Unique(), "buyer" + i, (sku, 1))))
                .ToArray());

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created),
                r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity }));
            Assert.Equal(0, await _client.StockAsync(sku));
            Assert.Equal(1, await CountOrdersForSkuAsync(sku));
        }
    }

    [Fact]
    public async Task The_same_key_sent_twice_at_once_creates_one_order_and_takes_stock_once()
    {
        for (var round = 0; round < 15; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 5);
            var key = "same-" + Http.Unique();
            var results = await Race.RunAsync(
                () => _client.PlaceAsync(key, "alice", (sku, 1)),
                () => _client.PlaceAsync(key, "alice", (sku, 1)));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            var other = results.Single(r => r.StatusCode != HttpStatusCode.Created);
            Assert.Contains(other.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            Assert.Equal(4, await _client.StockAsync(sku));
            Assert.Equal(1, await CountOrdersForKeyAsync(key));
            if (other.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("true", other.Headers.GetValues("Idempotent-Replayed").Single());
            }
        }
    }

    [Fact]
    public async Task The_same_key_sent_six_times_at_once_creates_one_order()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "same-" + Http.Unique();
        var results = await Race.RunAsync(Enumerable.Range(0, 6)
            .Select(_ => (Func<Task<HttpResponseMessage>>)(() => _client.PlaceAsync(key, "alice", (sku, 1)))).ToArray());

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(4, await _client.StockAsync(sku));
        Assert.Equal(1, await CountOrdersForKeyAsync(key));
    }

    [Fact]
    public async Task Cancel_and_ship_race_on_one_etag_has_exactly_one_winner()
    {
        for (var round = 0; round < 15; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 10);
            var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 3));
            var id = (await created.JsonBodyAsync()).GetProperty("id").GetInt64();
            var paid = await _client.ChangeStatusAsync(id, "PAID", created.ETagOf());
            var etag = paid.ETagOf();

            var results = await Race.RunAsync(
                () => _client.ChangeStatusAsync(id, "SHIPPED", etag),
                () => _client.ChangeStatusAsync(id, "CANCELLED", etag));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));

            var final = (await (await _client.GetAsync($"/orders/{id}")).JsonBodyAsync()).GetProperty("status").GetString();
            var stock = await _client.StockAsync(sku);
            if (final == "CANCELLED")
            {
                Assert.Equal(10, stock); // restocked exactly once
            }
            else
            {
                Assert.Equal("SHIPPED", final);
                Assert.Equal(7, stock); // shipped orders keep their reservation
            }
        }
    }

    [Fact]
    public async Task Two_cancels_on_one_etag_restock_only_once()
    {
        for (var round = 0; round < 10; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 10);
            var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 4));
            var id = (await created.JsonBodyAsync()).GetProperty("id").GetInt64();

            var results = await Race.RunAsync(
                () => _client.ChangeStatusAsync(id, "CANCELLED", created.ETagOf()),
                () => _client.ChangeStatusAsync(id, "CANCELLED", created.ETagOf()));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
            Assert.Equal(10, await _client.StockAsync(sku));
        }
    }

    [Fact]
    public async Task Buyers_who_do_not_compete_for_the_last_unit_all_succeed_through_retries()
    {
        var sku = await _client.SeedItemAsync(stock: 20);
        var results = await Race.RunAsync(Enumerable.Range(0, 5)
            .Select(i => (Func<Task<HttpResponseMessage>>)(() => _client.PlaceAsync($"k{i}-" + Http.Unique(), "buyer" + i, (sku, 2))))
            .ToArray());

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(10, await _client.StockAsync(sku));
        Assert.Equal(5, await CountOrdersForSkuAsync(sku));
    }

    private async Task<int> CountOrdersForSkuAsync(string sku)
    {
        var count = 0;
        await factory.WithDbAsync(async db => count = await db.Orders.CountAsync(o => o.Lines.Any(l => l.Sku == sku)));
        return count;
    }

    private async Task<int> CountOrdersForKeyAsync(string key)
    {
        var count = 0;
        await factory.WithDbAsync(async db => count = await db.Orders.CountAsync(o => o.IdempotencyKey == key));
        return count;
    }
}

/// <summary>The same races against a real database file (WAL), where connections are genuinely separate.</summary>
public class FileDatabaseConcurrencyTests(FileDbApiFactory factory) : IClassFixture<FileDbApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Last_unit_race_on_a_file_database()
    {
        for (var round = 0; round < 10; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 1);
            var results = await Race.RunAsync(
                () => _client.PlaceAsync("a-" + Http.Unique(), "alice", (sku, 1)),
                () => _client.PlaceAsync("b-" + Http.Unique(), "bob", (sku, 1)),
                () => _client.PlaceAsync("c-" + Http.Unique(), "carol", (sku, 1)));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created),
                r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity }));
            Assert.Equal(0, await _client.StockAsync(sku));
        }
    }

    [Fact]
    public async Task Same_key_twice_on_a_file_database()
    {
        for (var round = 0; round < 10; round++)
        {
            var sku = await _client.SeedItemAsync(stock: 5);
            var key = "same-" + Http.Unique();
            var results = await Race.RunAsync(
                () => _client.PlaceAsync(key, "alice", (sku, 1)),
                () => _client.PlaceAsync(key, "alice", (sku, 1)));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(4, await _client.StockAsync(sku));
        }
    }

    [Fact]
    public async Task The_file_database_is_in_wal_mode()
    {
        string? mode = null;
        await factory.WithDbAsync(async db =>
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync();
            command.CommandText = "PRAGMA journal_mode;";
            mode = (string?)await command.ExecuteScalarAsync();
        });
        Assert.Equal("wal", mode);
    }
}

/// <summary>
/// The concurrency token proven without any threads: a second DbContext updates the row behind the first one's back.
/// This holds even if the scheduler never overlaps two requests.
/// </summary>
public class ConcurrencyTokenTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task A_stale_item_write_is_rejected_and_the_first_write_survives()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        await using var first = factory.NewDb();
        await using var second = factory.NewDb();
        var itemInFirst = await first.Items.SingleAsync(i => i.Sku == sku);
        var itemInSecond = await second.Items.SingleAsync(i => i.Sku == sku);

        itemInSecond.Reserve(2);
        await second.SaveChangesAsync();

        itemInFirst.Reserve(3); // computed from the stale stock of 5
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());
        Assert.Equal(3, await _client.StockAsync(sku));
    }

    [Fact]
    public async Task Without_the_token_the_stale_write_would_have_overwritten_the_stock()
    {
        // Documents what the token prevents: a raw UPDATE that ignores Version loses the other writer's change.
        var sku = await _client.SeedItemAsync(stock: 5);
        await using var db = factory.NewDb();
        var stale = await db.Items.AsNoTracking().SingleAsync(i => i.Sku == sku);
        await using (var other = factory.NewDb())
        {
            (await other.Items.SingleAsync(i => i.Sku == sku)).Reserve(2);
            await other.SaveChangesAsync();
        }

        await db.Database.ExecuteSqlRawAsync("UPDATE Items SET Stock = {0} WHERE Sku = {1}", stale.Stock - 3, sku);
        Assert.Equal(2, await _client.StockAsync(sku)); // 5 - 3: the other writer's 2 units were lost
    }

    [Fact]
    public async Task A_stale_order_write_is_rejected()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var created = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
        var id = (await created.JsonBodyAsync()).GetProperty("id").GetInt64();
        await using var first = factory.NewDb();
        await using var second = factory.NewDb();
        var orderInFirst = await first.Orders.SingleAsync(o => o.Id == id);
        var orderInSecond = await second.Orders.SingleAsync(o => o.Id == id);

        orderInSecond.TransitionTo(OrderStatus.Cancelled);
        await second.SaveChangesAsync();

        orderInFirst.TransitionTo(OrderStatus.Paid);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());
        var get = await _client.GetAsync($"/orders/{id}");
        Assert.Equal("CANCELLED", (await get.JsonBodyAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_unique_index_on_the_idempotency_key_rejects_a_duplicate_and_is_classified_as_unique()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();
        await _client.PlaceAsync(key, "alice", (sku, 1));

        await using var db = factory.NewDb();
        var lines = new[] { OrderLine.Create(sku, 1, 1000) };
        db.Orders.Add(Order.Place(key, "x", "mallory", lines, PricingPolicy.Default.Quote(lines), factory.Time.GetUtcNow()));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(SqliteErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task A_non_unique_database_error_is_not_classified_as_a_unique_violation()
    {
        await using var db = factory.NewDb();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO Items (Sku) VALUES ('x')"));
        var update = new DbUpdateException("wrapped", ex);
        Assert.False(SqliteErrors.IsUniqueViolation(update));
        Assert.False(SqliteErrors.IsUniqueViolation(new DbUpdateException("no inner")));
    }
}

/// <summary>createdAt must be the same value everywhere: POST body, GET, replay, list, and the stored row.</summary>
public class TimestampTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string CreatedAt(System.Text.Json.JsonElement order) => order.GetProperty("createdAt").GetString()!;

    [Fact]
    public async Task Created_at_keeps_full_tick_precision_in_post_get_replay_list_and_the_database()
    {
        var instant = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(1234567);
        factory.Time.Set(instant);
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 5);
        var key = "key-" + Http.Unique();

        var post = await _client.PlaceAsync(key, customer, (sku, 1));
        var postBody = await post.JsonBodyAsync();
        var id = postBody.GetProperty("id").GetInt64();
        var get = await (await _client.GetAsync($"/orders/{id}")).JsonBodyAsync();
        var replay = await (await _client.PlaceAsync(key, customer, (sku, 1))).JsonBodyAsync();
        var list = await (await _client.GetAsync($"/orders?customerRef={customer}")).JsonBodyAsync();

        var posted = CreatedAt(postBody);
        Assert.Equal(posted, CreatedAt(get));
        Assert.Equal(posted, CreatedAt(replay));
        Assert.Equal(posted, CreatedAt(list.GetProperty("items")[0]));
        Assert.Equal(instant, DateTimeOffset.Parse(posted, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(instant.UtcTicks, DateTimeOffset.Parse(posted, System.Globalization.CultureInfo.InvariantCulture).UtcTicks);

        long storedTicks = 0;
        await factory.WithDbAsync(async db => storedTicks = (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id)).CreatedAt.UtcTicks);
        Assert.Equal(instant.UtcTicks, storedTicks);
    }

    [Fact]
    public async Task A_clock_that_returns_a_non_utc_offset_still_gives_identical_post_and_get_bodies()
    {
        var instant = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.FromHours(5.5)).AddTicks(7);
        factory.Time.Set(instant);
        var sku = await _client.SeedItemAsync(stock: 5);

        var post = await _client.PlaceAsync("key-" + Http.Unique(), "alice", (sku, 1));
        var id = (await post.JsonBodyAsync()).GetProperty("id").GetInt64();
        var get = await _client.GetAsync($"/orders/{id}");

        Assert.Equal((await post.JsonBodyAsync()).ToString(), (await get.JsonBodyAsync()).ToString());
        Assert.EndsWith("+00:00", CreatedAt(await post.JsonBodyAsync()));
    }

    [Fact]
    public async Task Two_instants_one_tick_apart_sort_correctly()
    {
        var customer = "c-" + Http.Unique();
        var sku = await _client.SeedItemAsync(stock: 5);
        var instant = new DateTimeOffset(2026, 5, 5, 5, 5, 5, TimeSpan.Zero);
        factory.Time.Set(instant.AddTicks(1));
        var later = (await (await _client.PlaceAsync("k1-" + Http.Unique(), customer, (sku, 1))).JsonBodyAsync()).GetProperty("id").GetInt64();
        factory.Time.Set(instant);
        var earlier = (await (await _client.PlaceAsync("k2-" + Http.Unique(), customer, (sku, 1))).JsonBodyAsync()).GetProperty("id").GetInt64();

        var list = await (await _client.GetAsync($"/orders?customerRef={customer}")).JsonBodyAsync();
        Assert.Equal([later, earlier], list.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("id").GetInt64()));
    }
}

/// <summary>Everything that can go wrong at the HTTP edge answers with the same problem document.</summary>
public class ProblemShapeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // A literal on purpose: the first mutation run showed that tests built on RequestBody.MaxBytes cannot notice that constant being raised.
    private const int BodyLimit = 64 * 1024;

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Unknown_route_is_a_404_problem() =>
        await (await _client.GetAsync("/nope")).AssertProblemAsync(HttpStatusCode.NotFound, "not-found");

    [Fact]
    public async Task Wrong_method_is_a_405_problem() =>
        await (await _client.DeleteAsync("/orders/1")).AssertProblemAsync(HttpStatusCode.MethodNotAllowed, "method-not-allowed");

    [Fact]
    public async Task Wrong_content_type_is_a_415_problem()
    {
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), "{}", "text/plain");
        await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem_with_a_path()
    {
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), """{"customerRef": "x", "lines": [""");
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid-json");
        Assert.Single(problem.FieldErrors());
        Assert.Equal("invalid-json", problem.FieldErrors()[0].Code);
    }

    [Fact]
    public async Task A_wrongly_typed_field_names_its_json_path()
    {
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), """{"customerRef":"x","lines":[{"sku":"A","quantity":"many"}]}""");
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid-json");
        Assert.Equal("$.lines[0].quantity", problem.FieldErrors()[0].Field);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_null_or_empty_body_is_a_validation_problem(string json)
    {
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), json);
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("body", "required")], problem.FieldErrors());
    }

    [Fact]
    public async Task A_utf8_byte_order_mark_is_tolerated()
    {
        var sku = await _client.SeedItemAsync(stock: 5);
        var json = Encoding.UTF8.GetBytes($$"""{"customerRef":"alice","lines":[{"sku":"{{sku}}","quantity":1}]}""");
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. json]) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "bom-" + Http.Unique());
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_json_array_instead_of_an_object_is_a_400()
    {
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), "[]");
        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid-json");
    }

    [Fact]
    public async Task An_oversized_body_is_a_413_problem()
    {
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), new string(' ', BodyLimit + 1));
        await response.AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
    }

    [Fact]
    public async Task A_body_of_exactly_the_limit_is_read_and_judged_on_content()
    {
        var padding = new string(' ', BodyLimit - 2);
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(), "{}" + padding);
        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task A_chunked_oversized_body_is_also_a_413()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', BodyLimit + 10)))),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "k-" + Http.Unique());
        await (await _client.SendAsync(request)).AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
    }

    [Fact]
    public async Task Problem_documents_carry_a_trace_id_and_the_instance_path()
    {
        var problem = await (await _client.GetAsync("/orders/424242")).AssertProblemAsync(HttpStatusCode.NotFound, "order-not-found");
        Assert.Equal("/orders/424242", problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task Healthz_is_200_ok()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.JsonBodyAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_item_with_a_json_array_is_a_400()
    {
        var response = await _client.PostAsync("/items", new StringContent("[1]", Encoding.UTF8, "application/json"));
        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid-json");
    }

    [Fact]
    public async Task Post_items_without_a_body_is_a_400()
    {
        var response = await _client.PostAsync("/items", new StringContent("", Encoding.UTF8, "application/json"));
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([("body", "required")], problem.FieldErrors());
    }

    [Fact]
    public async Task Json_content_types_with_a_suffix_are_accepted()
    {
        var sku = await _client.SeedItemAsync();
        var response = await _client.PlaceRawAsync("k-" + Http.Unique(),
            $$"""{"customerRef":"alice","lines":[{"sku":"{{sku}}","quantity":1}]}""", "application/vnd.orders+json");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
