using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace OrdersApi.Infrastructure;

public static class DatabaseInitializer
{
    /// <summary>
    /// Creates the schema if the database is empty (EnsureCreated, no migrations: this is a showcase, not a
    /// schema-evolution demo). File databases are switched to WAL so readers do not block the single writer.
    /// </summary>
    public static async Task InitializeAsync(OrdersDbContext db, string connectionString)
    {
        await db.Database.EnsureCreatedAsync();

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode != SqliteOpenMode.Memory && !string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal))
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
    }
}
