using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace UkuuHr.Data;

/// <summary>
/// Idempotent schema migrations: adds columns the EF model expects but that
/// legacy databases may be missing (e.g. a database created by an older model).
/// Column existence is checked first (<see cref="ColumnExistsAsync"/>), then a
/// plain ADD COLUMN runs — this works on both PostgreSQL and SQLite (the local
/// dev fallback DB, which rejects PostgreSQL's "ADD COLUMN IF NOT EXISTS"
/// syntax).
/// </summary>
public static class IdempotentMigrationRunner
{
    /// <summary>
    /// Runs each pending migration. Every step is wrapped in try/catch so a
    /// failure (e.g. a concurrent instance adding the column between the
    /// existence check and the ALTER) is logged and skipped without stopping
    /// application startup.
    /// </summary>
    public static async Task RunIdempotentMigrationsAsync(UkuuHrDbContext db, ILogger logger, bool useSqlite)
    {
        var migrations = new (string Table, string Column, string AddSql)[]
        {
            ("LeaveHolidays", "IsRecurring",
                @"ALTER TABLE ""LeaveHolidays"" ADD COLUMN ""IsRecurring"" boolean NOT NULL DEFAULT false"),
            ("Employees", "PayrollId",
                @"ALTER TABLE ""Employees"" ADD COLUMN ""PayrollId"" varchar(50)"),
            ("AttendanceDevices", "UseHttps",
                @"ALTER TABLE ""AttendanceDevices"" ADD COLUMN ""UseHttps"" boolean NOT NULL DEFAULT false"),
        };

        foreach (var (table, column, addSql) in migrations)
        {
            try
            {
                if (await ColumnExistsAsync(db, table, column, useSqlite))
                {
                    logger.LogInformation("Migration: {Table}.{Column} already present", table, column);
                    continue;
                }
                await db.Database.ExecuteSqlRawAsync(addSql);
                logger.LogInformation("Migration: added {Table}.{Column}", table, column);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Migration failed for {Table}.{Column}: {Message}", table, column, ex.Message);
            }
        }
    }

    /// <summary>
    /// Returns true when the given column already exists on the table.
    /// Table/column names passed here are compile-time constants declared in
    /// <see cref="RunIdempotentMigrationsAsync"/> — never user input — so there
    /// is no SQL-injection surface. (EF1002 on the interpolated queries below
    /// is a false positive.)
    /// </summary>
    public static async Task<bool> ColumnExistsAsync(UkuuHrDbContext db, string table, string column, bool useSqlite)
    {
#pragma warning disable EF1002
        if (useSqlite)
        {
            // pragma_table_info() is a table-valued function — read its "name" column.
            var names = await db.Database.SqlQueryRaw<string>($"SELECT name FROM pragma_table_info('{table}')").ToListAsync();
            return names.Any(n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase));
        }
        // PostgreSQL — information_schema.columns.
        var count = await db.Database.SqlQueryRaw<int>(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}') THEN 1 ELSE 0 END").ToListAsync();
        return count.FirstOrDefault() > 0;
#pragma warning restore EF1002
    }
}
