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
    /// Returns true when the given table already exists.
    /// Names passed here are compile-time constants — no SQL-injection surface.
    /// </summary>
    public static async Task<bool> TableExistsAsync(UkuuHrDbContext db, string table, bool useSqlite)
    {
#pragma warning disable EF1002
        if (useSqlite)
        {
            var names = await db.Database.SqlQueryRaw<string>(
                $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'").ToListAsync();
            return names.Count > 0;
        }
        var count = await db.Database.SqlQueryRaw<int>(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '{table}') THEN 1 ELSE 0 END").ToListAsync();
        return count.FirstOrDefault() > 0;
#pragma warning restore EF1002
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
