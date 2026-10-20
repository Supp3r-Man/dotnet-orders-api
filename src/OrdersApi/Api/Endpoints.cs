using System.Globalization;
using OrdersApi.Application;
using OrdersApi.Infrastructure;

namespace OrdersApi.Api;

public static class Endpoints
{
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", Health);

        app.MapPost("/items", CreateItem);
        app.MapGet("/items/{sku}", GetItem);

        app.MapPost("/orders", PlaceOrder);
        app.MapGet("/orders", ListOrders);
        app.MapGet("/orders/{id}", GetOrder);
        app.MapPatch("/orders/{id}/status", ChangeStatus);
    }

    private static async Task<IResult> Health(OrdersDbContext db, CancellationToken ct)
    {
        if (await db.Database.CanConnectAsync(ct))
        {
            return Results.Ok(new { status = "ok" });
        }

        throw new AppException(ErrorKind.Unavailable, "database-unavailable", "The database cannot be reached.");
    }

    private static async Task<IResult> CreateItem(HttpContext ctx, CatalogService catalog)
    {
        var input = await RequestBody.ReadJsonAsync<CreateItemInput>(ctx.Request, ctx.RequestAborted);
        var item = await catalog.CreateAsync(input, ctx.RequestAborted);
        return Results.Created($"/items/{item.Sku}", ItemResponse.From(item));
    }

    private static async Task<IResult> GetItem(string sku, HttpContext ctx, CatalogService catalog) =>
        Results.Ok(ItemResponse.From(await catalog.GetAsync(sku, ctx.RequestAborted)));

    private static async Task<IResult> PlaceOrder(HttpContext ctx, OrderService orders)
    {
        var input = await RequestBody.ReadJsonAsync<CreateOrderInput>(ctx.Request, ctx.RequestAborted);
        var keyValues = ctx.Request.Headers[Validation.IdempotencyKeyHeader];
        if (keyValues.Count > 1)
        {
            // Found by running the app: two header lines used to be silently merged into one "a,b" key.
            throw AppException.Validation(Validation.IdempotencyKeyHeader, "multiple-values",
                $"Send the {Validation.IdempotencyKeyHeader} header exactly once.");
        }

        var key = keyValues.ToString();
        var placement = await orders.PlaceAsync(key.Length == 0 ? null : key, input, ctx.RequestAborted);

        var body = OrderResponse.From(placement.Order);
        ctx.Response.Headers.ETag = ETags.ForVersion(placement.Order.Version);
        if (placement.Replayed)
        {
            ctx.Response.Headers[ReplayedHeader] = "true";
            ctx.Response.Headers.ContentLocation = $"/orders/{body.Id}";
            return Results.Ok(body);
        }

        return Results.Created($"/orders/{body.Id}", body);
    }

    private static async Task<IResult> GetOrder(string id, HttpContext ctx, OrderService orders)
    {
        var order = await orders.GetAsync(ParseId(id), ctx.RequestAborted);
        var etag = ETags.ForVersion(order.Version);
        ctx.Response.Headers.ETag = etag;

        var ifNoneMatch = ParsePrecondition(ctx.Request, "If-None-Match");
        if (ifNoneMatch is not null && ifNoneMatch.MatchesWeak(etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Ok(OrderResponse.From(order));
    }

    private static async Task<IResult> ListOrders(HttpContext ctx, OrderService orders)
    {
        var q = ctx.Request.Query;
        var query = Validation.ValidateListQuery(q["status"], q["customerRef"], q["createdAfter"], q["page"], q["size"]);
        var page = await orders.ListAsync(query, ctx.RequestAborted);
        return Results.Ok(new PageResponse<OrderResponse>(
            page.Items.Select(OrderResponse.From).ToList(), page.Page, page.Size, page.TotalCount, page.TotalPages));
    }

    private static async Task<IResult> ChangeStatus(string id, HttpContext ctx, OrderService orders)
    {
        var orderId = ParseId(id);
        var body = await RequestBody.ReadJsonAsync<ChangeStatusRequest>(ctx.Request, ctx.RequestAborted);
        if (body is null)
        {
            throw AppException.Validation("body", "required", "A JSON request body is required.");
        }

        var target = Validation.ValidateStatus(body.Status);
        var ifMatch = ParsePrecondition(ctx.Request, "If-Match");
        var order = await orders.ChangeStatusAsync(orderId, ifMatch, target, ctx.RequestAborted);

        ctx.Response.Headers.ETag = ETags.ForVersion(order.Version);
        return Results.Ok(OrderResponse.From(order));
    }

    private static long ParseId(string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
        {
            throw AppException.Validation("id", "invalid-value", "id must be a positive whole number.");
        }

        return id;
    }

    /// <summary>Null when the header is absent; a 400 when it is present but malformed.</summary>
    private static EntityTagPrecondition? ParsePrecondition(HttpRequest request, string header)
    {
        if (!request.Headers.TryGetValue(header, out var values))
        {
            return null;
        }

        if (!EntityTagPrecondition.TryParse(values.ToString(), out var parsed))
        {
            throw AppException.Validation(header, "invalid-value",
                $"{header} must be * or a list of entity tags such as \"3\".");
        }

        return parsed;
    }
}
