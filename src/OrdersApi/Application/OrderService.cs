using Microsoft.EntityFrameworkCore;
using OrdersApi.Domain;
using OrdersApi.Infrastructure;

namespace OrdersApi.Application;

public sealed class OrderService(OrdersDbContext db, TimeProvider clock, PricingPolicy pricing)
{
    /// <summary>
    /// How often a request re-reads and retries after losing an optimistic-concurrency race. Each lost round means
    /// another writer committed, so with k contenders a request needs at most k rounds. Past the cap the answer is 409.
    /// </summary>
    public const int MaxAttempts = 8;

    public async Task<Placement> PlaceAsync(string? idempotencyKey, CreateOrderInput? input, CancellationToken ct)
    {
        var request = Validation.ValidateOrder(idempotencyKey, input);
        var hash = RequestFingerprint.Compute(request);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var existing = await db.Orders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != hash)
                {
                    throw new AppException(ErrorKind.Unprocessable, "idempotency-key-reuse",
                        "This Idempotency-Key was already used with a different request body.");
                }

                return new Placement(existing, Replayed: true);
            }

            var skus = request.Lines.Select(l => l.Sku).ToList();
            var items = await db.Items.Where(i => skus.Contains(i.Sku)).ToDictionaryAsync(i => i.Sku, ct);
            var unknown = request.Lines
                .Select((line, index) => (line, index))
                .Where(x => !items.ContainsKey(x.line.Sku))
                .Select(x => new FieldError($"lines[{x.index}].sku", "unknown-sku", $"No item with SKU {x.line.Sku}."))
                .ToList();
            if (unknown.Count > 0)
            {
                throw new AppException(ErrorKind.Unprocessable, "unknown-sku", "One or more SKUs do not exist.", unknown);
            }

            Order order;
            try
            {
                var lines = request.Lines
                    .Select(l => OrderLine.Create(l.Sku, l.Quantity, items[l.Sku].UnitPriceMinor))
                    .ToList();
                var quote = pricing.Quote(lines);
                foreach (var line in request.Lines)
                {
                    items[line.Sku].Reserve(line.Quantity);
                }

                order = Order.Place(request.IdempotencyKey, hash, request.CustomerRef, lines, quote, clock.GetUtcNow());
            }
            catch (DomainException ex)
            {
                throw AppException.From(ex);
            }

            db.Orders.Add(order);
            try
            {
                await db.SaveChangesAsync(ct);
                return new Placement(order, Replayed: false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone changed a stock row (or inserted this key) between our read and our write.
                // Forget everything we loaded and start over: the next round sees the new stock or the new order.
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
            {
                throw new AppException(ErrorKind.Conflict, "idempotency-key-conflict",
                    "Another request with this Idempotency-Key is being processed. Retry shortly.");
            }
        }

        throw ConcurrentUpdate();
    }

    public async Task<Order> GetAsync(long id, CancellationToken ct) =>
        await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw OrderNotFound(id);

    public async Task<PagedResult<Order>> ListAsync(OrderListQuery query, CancellationToken ct)
    {
        IQueryable<Order> orders = db.Orders.AsNoTracking();
        if (query.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (query.CustomerRef is { } customer)
        {
            orders = orders.Where(o => o.CustomerRef == customer);
        }

        if (query.CreatedAfter is { } after)
        {
            orders = orders.Where(o => o.CreatedAt > after);
        }

        var total = await orders.LongCountAsync(ct);

        // Newest first. Orders created in the same tick share CreatedAt, so Id breaks the tie: without it two
        // pages can repeat or skip a row because the database is free to order equal keys differently per query.
        var items = await orders
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip(query.Page * query.Size).Take(query.Size)
            .Include(o => o.Lines)
            .ToListAsync(ct);
        return new PagedResult<Order>(items, query.Page, query.Size, total);
    }

    public async Task<Order> ChangeStatusAsync(long id, EntityTagPrecondition? ifMatch, OrderStatus target, CancellationToken ct)
    {
        if (ifMatch is null)
        {
            throw new AppException(ErrorKind.PreconditionRequired, "precondition-required",
                "The If-Match header is required for status changes. GET the order and send its ETag.");
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct)
                        ?? throw OrderNotFound(id);

            // After a lost race this re-read sees the winner's version, so the stale If-Match now fails here: 412.
            if (!ifMatch.MatchesStrong(ETags.ForVersion(order.Version)))
            {
                throw new AppException(ErrorKind.PreconditionFailed, "precondition-failed",
                    "The order changed since you read it (If-Match does not match the current ETag). GET it again.");
            }

            try
            {
                order.TransitionTo(target);
                if (target == OrderStatus.Cancelled)
                {
                    var skus = order.Lines.Select(l => l.Sku).ToList();
                    var items = await db.Items.Where(i => skus.Contains(i.Sku)).ToDictionaryAsync(i => i.Sku, ct);
                    foreach (var line in order.Lines)
                    {
                        items[line.Sku].Restock(line.Quantity);
                    }
                }
            }
            catch (DomainException ex)
            {
                throw AppException.From(ex);
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return order;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }

        throw ConcurrentUpdate();
    }

    private static AppException OrderNotFound(long id) => AppException.NotFound("order-not-found", $"No order with id {id}.");

    private static AppException ConcurrentUpdate() => new(ErrorKind.Conflict, "concurrent-update",
        "The request lost repeated races with other writers. Retry.");
}
