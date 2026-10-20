using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrdersApi.Infrastructure;

namespace OrdersApi.Tests;

/// <summary>
/// The real app on TestServer with its own SQLite database. Default: a named shared in-memory database kept
/// alive by one open "keeper" connection (each request still opens its own connection, so requests really overlap).
/// xunit creates one factory per test class (IClassFixture), so every class has an isolated database.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection? _keeper;
    private readonly string? _file;

    public ApiFactory() : this(useFile: false)
    {
    }

    protected ApiFactory(bool useFile)
    {
        var id = Guid.NewGuid().ToString("N");
        if (useFile)
        {
            _file = Path.Combine(Path.GetTempPath(), $"orders-test-{id}.db");
            ConnectionString = $"Data Source={_file}";
        }
        else
        {
            ConnectionString = $"Data Source=orders-{id};Mode=Memory;Cache=Shared";
            _keeper = new SqliteConnection(ConnectionString);
            _keeper.Open();
        }
    }

    public string ConnectionString { get; }

    public TestClock Time { get; } = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Hook for a test class to add or replace services (for example a SaveChanges interceptor).</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Orders", ConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            ConfigureTestServices(services);
        });
    }

    public async Task WithDbAsync(Func<OrdersDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<OrdersDbContext>());
    }

    /// <summary>A second, independent DbContext on the same database (a "second process").</summary>
    public OrdersDbContext NewDb() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlite(ConnectionString).Options);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keeper?.Dispose();
            if (_file is not null)
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { _file, _file + "-wal", _file + "-shm" })
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                        // best effort; the file lives in the temp folder
                    }
                }
            }
        }
    }
}

/// <summary>Same app on a temporary database file (WAL), used where the file path itself matters.</summary>
public sealed class FileDbApiFactory : ApiFactory
{
    public FileDbApiFactory() : base(useFile: true)
    {
    }
}

public static class Http
{
    public static string Unique() => Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    public static async Task<JsonElement> JsonBodyAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public static async Task<string> SeedItemAsync(this HttpClient client, string? sku = null, long price = 1000, int stock = 10)
    {
        sku ??= "SKU-" + Unique();
        var response = await client.PostAsJsonAsync("/items", new { sku, name = "Item " + sku, unitPriceMinor = price, stock });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return sku;
    }

    public static Task<HttpResponseMessage> PlaceAsync(this HttpClient client, string? key, string customer,
        params (string Sku, int Qty)[] lines)
    {
        var body = JsonSerializer.Serialize(new
        {
            customerRef = customer,
            lines = lines.Select(l => new { sku = l.Sku, quantity = l.Qty }),
        });
        return client.PlaceRawAsync(key, body);
    }

    public static Task<HttpResponseMessage> PlaceRawAsync(this HttpClient client, string? key, string json,
        string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, contentType),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> ChangeStatusAsync(this HttpClient client, long id, string? status, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/orders/{id}/status")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { status }), Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    public static string ETagOf(this HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("No ETag header.");

    public static async Task<int> StockAsync(this HttpClient client, string sku)
    {
        var response = await client.GetAsync($"/items/{sku}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonBodyAsync()).GetProperty("stock").GetInt32();
    }

    /// <summary>Asserts the one error shape: problem+json with code, errors[], type, title, status, traceId.</summary>
    public static async Task<JsonElement> AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.JsonBodyAsync();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal("urn:orders-api:problem:" + code, body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.Equal(JsonValueKind.Array, body.GetProperty("errors").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.True(body.TryGetProperty("instance", out _));
        return body;
    }

    public static (string Field, string Code)[] FieldErrors(this JsonElement problem) =>
        problem.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("field").GetString()!, e.GetProperty("code").GetString()!))
            .ToArray();
}

/// <summary>A TimeProvider a test can set to any instant (FakeTimeProvider refuses to go backwards).</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    private readonly object _gate = new();

    public void Set(DateTimeOffset value) { lock (_gate) { _now = value; } }

    public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }
}
