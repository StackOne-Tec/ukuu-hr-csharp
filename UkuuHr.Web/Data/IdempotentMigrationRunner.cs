using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace UkuuHr.Data;

/// <summary>
/// Idempotent schema migrations: adds columns/tables the EF model expects but that
/// legacy databases may be missing (e.g. a database created by an older model).
///
/// P0 reliability fix: existence checks now PROBE THE SCHEMA DIRECTLY by selecting
/// the column/table (`SELECT "Col" FROM "Table" LIMIT 0`). The previous
/// information_schema-based check could FALSE-POSITIVE on shared managed Postgres
/// (e.g. Prisma Postgres), where information_schema.columns lists columns from
/// every schema the role can see — causing a required column to be skipped and
/// every EF query selecting it to fail with 500s until the next heal. A SELECT
/// probe resolves through the connection's search_path and cannot lie.
/// </summary>
public static class IdempotentMigrationRunner
{
    /// <summary>
    /// Runs each pending migration. Every step is wrapped in try/catch so a
    /// failure is logged and skipped without stopping application startup, and
    /// each addition is VERIFIED afterwards — a column that is still missing
    /// after the heal attempt is logged as an error (visible in host logs).
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
            // P2/H-5: Encrypted device password column
            ("AttendanceDevices", "PasswordEncrypted",
                @"ALTER TABLE ""AttendanceDevices"" ADD COLUMN ""PasswordEncrypted"" varchar(512)"),
            // Attendance manual-correction support: UpdatedAt audit timestamp
            ("Attendances", "UpdatedAt",
                @"ALTER TABLE ""Attendances"" ADD COLUMN ""UpdatedAt"" timestamp NULL"),
            // Branch/location support: employee branch assignment
            ("Employees", "BranchId",
                @"ALTER TABLE ""Employees"" ADD COLUMN ""BranchId"" integer NULL"),
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

                // Verify the heal actually landed (belt & braces).
                if (await ColumnExistsAsync(db, table, column, useSqlite))
                {
                    logger.LogInformation("Migration: added {Table}.{Column}", table, column);
                }
                else
                {
                    logger.LogError(
                        "Migration CRITICAL: {AddSql} executed but {Table}.{Column} is STILL NOT accessible. " +
                        "EF queries selecting this column will fail until the database schema is repaired.",
                        addSql, table, column);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Migration failed for {Table}.{Column}: {Message}", table, column, ex.Message);
            }
        }

        // New tables (dialect-specific DDL — both support IF NOT EXISTS).
        if (!await TableExistsAsync(db, "Branches", useSqlite))
        {
            try
            {
                var createBranches = useSqlite
                    ? @"CREATE TABLE IF NOT EXISTS ""Branches"" (
                            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                            ""OrganizationId"" INTEGER NOT NULL,
                            ""Name"" TEXT NOT NULL,
                            ""City"" TEXT NULL,
                            ""Address"" TEXT NULL,
                            ""ContactPhone"" TEXT NULL,
                            ""IsActive"" INTEGER NOT NULL DEFAULT 1,
                            ""CreatedAt"" TEXT NOT NULL,
                            ""UpdatedAt"" TEXT NULL)"
                    : @"CREATE TABLE IF NOT EXISTS ""Branches"" (
                            ""Id"" serial PRIMARY KEY,
                            ""OrganizationId"" integer NOT NULL,
                            ""Name"" varchar(150) NOT NULL,
                            ""City"" varchar(100) NULL,
                            ""Address"" varchar(250) NULL,
                            ""ContactPhone"" varchar(100) NULL,
                            ""IsActive"" boolean NOT NULL DEFAULT true,
                            ""CreatedAt"" timestamp NOT NULL,
                            ""UpdatedAt"" timestamp NULL)";
                await db.Database.ExecuteSqlRawAsync(createBranches);
                logger.LogInformation("Migration: created Branches table");
            }
            catch (Exception ex)
            {
                logger.LogWarning("Branches table migration failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Returns true when the given table already exists AND is selectable.
    /// SQLite: sqlite_master lookup. Postgres: probed by selecting from the table
    /// (resolves through the connection's search_path — cannot false-positive
    /// from other schemas on shared managed instances).
    /// </summary>
    public static async Task<bool> TableExistsAsync(UkuuHrDbContext db, string table, bool useSqlite)
    {
#pragma warning disable EF1002
        try
        {
            if (useSqlite)
            {
                var names = await db.Database.SqlQueryRaw<string>(
                    $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'").ToListAsync();
                return names.Count > 0;
            }
            // Postgres — LIMIT 0 touches no rows but fully resolves the table.
            await db.Database.ExecuteSqlRawAsync($"SELECT 1 FROM \"{table}\" LIMIT 0");
            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore EF1002
    }

    /// <summary>
    /// Returns true when the given column already exists on the table.
    /// SQLite: pragma_table_info (accurate — do NOT use a SELECT probe here:
    /// SQLite's double-quote fallback silently reinterprets an unknown
    /// "Identifier" as a string literal, so the probe would always succeed).
    /// Postgres: SELECT the column itself — the definitive, schema-aware test
    /// matching exactly what EF's generated queries require.
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
            var names = await db.Database.SqlQueryRaw<string>(
                $"SELECT name FROM pragma_table_info('{table}')").ToListAsync();
            return names.Any(n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase));
        }
        try
        {
            // Postgres — quoted identifiers must exist; no string-literal fallback.
            await db.Database.ExecuteSqlRawAsync($"SELECT \"{column}\" FROM \"{table}\" LIMIT 0");
            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore EF1002
    }
}
