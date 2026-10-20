using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrdersApi.Api;
using OrdersApi.Application;
using OrdersApi.Domain;
using OrdersApi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Resolved lazily (inside the options callback) so a test host can override the connection string.
builder.Services.AddDbContext<OrdersDbContext>((sp, options) =>
    options.UseSqlite(ConnectionString(sp.GetRequiredService<IConfiguration>()))
        .AddInterceptors(sp.GetServices<IInterceptor>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(PricingPolicy.Default);
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Normalize);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Found by running the app: Kestrel answered a non-ASCII header value with an empty 400 before our pipeline ran.
// Decoding headers as UTF-8 lets the Idempotency-Key validator produce the normal problem document instead.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.RequestHeaderEncodingSelector = _ => System.Text.Encoding.UTF8);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseInitializer.InitializeAsync(
        scope.ServiceProvider.GetRequiredService<OrdersDbContext>(), ConnectionString(app.Configuration));
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapApi();

app.Run();

static string ConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("Orders") ?? "Data Source=orders.db";

// Needed so WebApplicationFactory<Program> can see the entry point.
public partial class Program;
