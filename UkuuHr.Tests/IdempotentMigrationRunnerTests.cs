using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UkuuHr.Data;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Unit tests for <see cref="IdempotentMigrationRunner"/> (Phase 30 — the
/// startup schema migrations that bring legacy databases up to the current EF
/// model). All tests run against a real SQLite database file, exercising the
/// exact same SQL paths the app uses on the local-dev fallback DB.
/// The PostgreSQL branch (useSqlite: false, information_schema) is only
/// exercised at deploy time — it needs a live Postgres, which CI lacks.
/// </summary>
public class IdempotentMigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly UkuuHrDbContext _db;

    public IdempotentMigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ukuu-migration-{Guid.NewGuid():N}.db");
        _db = new UkuuHrDbContext(
            new DbContextOptionsBuilder<UkuuHrDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    private async Task<List<string>> ColumnNamesAsync(string table)
    {
        // Table names are hard-coded test constants — no injection surface.
#pragma warning disable EF1002
        var names = await _db.Database.SqlQueryRaw<string>($"SELECT name FROM pragma_table_info('{table}')").ToListAsync();
        return names;
#pragma warning restore EF1002
    }

    // ───────────── ColumnExistsAsync ─────────────

    [Fact]
    public async Task ColumnExists_ReturnsTrue_ForColumns_In_Current_Schema()
    {
        await _db.Database.EnsureCreatedAsync();

        Assert.True(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "AttendanceDevices", "UseHttps", useSqlite: true));
        Assert.True(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "Employees", "PayrollId", useSqlite: true));
        Assert.True(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "LeaveHolidays", "IsRecurring", useSqlite: true));
    }

    [Fact]
    public async Task ColumnExists_ReturnsFalse_ForMissing_Column()
    {
        await _db.Database.EnsureCreatedAsync();

        Assert.False(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "Employees", "DefinitelyNotAColumn", useSqlite: true));
        Assert.False(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "DoesNotExistTable", "AnyColumn", useSqlite: true));
    }

    [Fact]
    public async Task ColumnExists_IsCaseInsensitive_OnSqlite()
    {
        await _db.Database.EnsureCreatedAsync();

        // SQLite stores identifiers with their original casing; the check must
        // match regardless of the caller's casing.
        Assert.True(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "attendanceDevices", "usehttps", useSqlite: true));
        Assert.True(await IdempotentMigrationRunner.ColumnExistsAsync(_db, "employees", "payrollid", useSqlite: true));
    }

    // ───────────── RunIdempotentMigrationsAsync ─────────────

    [Fact]
    public async Task RunMigrations_AddsMissingColumns_ToLegacyDatabase()
    {
        // A legacy database created by an older EF model — the three tables
        // exist but are missing the columns the current model expects.
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "LeaveHolidays" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_LeaveHolidays" PRIMARY KEY,
                "OrganizationId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Date" TEXT NOT NULL);
            CREATE TABLE "Employees" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Employees" PRIMARY KEY,
                "OrganizationId" INTEGER NOT NULL,
                "EmployeeCode" TEXT NOT NULL,
                "FirstName" TEXT NOT NULL,
                "Surname" TEXT NOT NULL,
                "Status" TEXT NOT NULL);
            CREATE TABLE "AttendanceDevices" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AttendanceDevices" PRIMARY KEY,
                "OrganizationId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Vendor" TEXT NOT NULL,
                "Mode" TEXT NOT NULL);
            """);

        await IdempotentMigrationRunner.RunIdempotentMigrationsAsync(_db, NullLogger.Instance, useSqlite: true);

        // The previously-missing columns must now exist with the expected defaults.
        Assert.Contains("IsRecurring", await ColumnNamesAsync("LeaveHolidays"));
        Assert.Contains("PayrollId", await ColumnNamesAsync("Employees"));
        Assert.Contains("UseHttps", await ColumnNamesAsync("AttendanceDevices"));
        Assert.DoesNotContain("IsRecurring", await ColumnNamesAsync("Employees"));
    }

    [Fact]
    public async Task RunMigrations_IsIdempotent_WhenRunTwice()
    {
        await _db.Database.EnsureCreatedAsync();

        await IdempotentMigrationRunner.RunIdempotentMigrationsAsync(_db, NullLogger.Instance, useSqlite: true);
        // Second run — every column already present, must be a no-op with no exception.
        await IdempotentMigrationRunner.RunIdempotentMigrationsAsync(_db, NullLogger.Instance, useSqlite: true);

        Assert.Contains("UseHttps", await ColumnNamesAsync("AttendanceDevices"));
        Assert.Contains("PayrollId", await ColumnNamesAsync("Employees"));
        Assert.Contains("IsRecurring", await ColumnNamesAsync("LeaveHolidays"));
    }

    [Fact]
    public async Task RunMigrations_IsSafe_WhenTable_DoesNotExist()
    {
        await _db.Database.EnsureCreatedAsync();
        await _db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"LeaveHolidays\"");

        // Dropping the table simulates a schema where a migration's table is
        // absent — the runner must log the failure and keep going, not throw.
        await IdempotentMigrationRunner.RunIdempotentMigrationsAsync(_db, NullLogger.Instance, useSqlite: true);

        Assert.Contains("PayrollId", await ColumnNamesAsync("Employees"));
        Assert.Contains("UseHttps", await ColumnNamesAsync("AttendanceDevices"));
    }
}
