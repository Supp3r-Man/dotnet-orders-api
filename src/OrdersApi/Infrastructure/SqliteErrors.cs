using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace OrdersApi.Infrastructure;

public static class SqliteErrors
{
    private const int SqliteConstraint = 19;
    private const int ConstraintPrimaryKey = 1555;
    private const int ConstraintUnique = 2067;

    /// <summary>True when the failed save was a UNIQUE or PRIMARY KEY violation (SQLite result code 19).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint } sqlite
        && sqlite.SqliteExtendedErrorCode is ConstraintUnique or ConstraintPrimaryKey;
}
