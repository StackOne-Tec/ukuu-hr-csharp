using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// HolidayService — CRUD + CSV import for FR-008 Holiday Management.
// Holidays feed the OvertimeService (FR-007 classification) and the
// ShiftEngine (holiday-aware status computation).
// ─────────────────────────────────────────────────────────────────────────────

public class HolidayService
{
    private readonly UkuuHrDbContext _db;
    private readonly AuditService _audit;

    public HolidayService(UkuuHrDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<List<LeaveHoliday>> GetAllAsync(int orgId, int? year = null)
    {
        var q = _db.LeaveHolidays.Where(h => h.OrganizationId == orgId);
        if (year.HasValue) q = q.Where(h => h.Date.Year == year.Value);
        return q.OrderByDescending(h => h.Date).ToListAsync();
    }

    public Task<LeaveHoliday?> GetAsync(int orgId, int id) =>
        _db.LeaveHolidays.FirstOrDefaultAsync(h => h.OrganizationId == orgId && h.Id == id);

    public async Task<LeaveHoliday> CreateAsync(int orgId, LeaveHoliday holiday, string? actorEmail)
    {
        holiday.OrganizationId = orgId;
        holiday.Date = holiday.Date.Date;
        // Prevent duplicates within org.
        var exists = await _db.LeaveHolidays.AnyAsync(h =>
            h.OrganizationId == orgId && h.Date.Date == holiday.Date.Date);
        if (exists)
            throw new InvalidOperationException($"A holiday on {holiday.Date:yyyy-MM-dd} already exists.");

        _db.LeaveHolidays.Add(holiday);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.BulkImport, actorEmail,
            details: $"Created holiday '{holiday.Name}' on {holiday.Date:yyyy-MM-dd}",
            newValue: $"{holiday.Name} ({holiday.Date:yyyy-MM-dd})");
        return holiday;
    }

    public async Task<LeaveHoliday> UpdateAsync(int orgId, LeaveHoliday updated, string? actorEmail)
    {
        var existing = await _db.LeaveHolidays.FirstOrDefaultAsync(h => h.OrganizationId == orgId && h.Id == updated.Id)
            ?? throw new InvalidOperationException("Holiday not found.");
        var prevName = existing.Name;
        var prevDate = existing.Date;

        existing.Name = updated.Name;
        existing.Date = updated.Date.Date;
        existing.Country = updated.Country;

        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.ProfileUpdated, actorEmail,
            details: $"Updated holiday '{prevName}'",
            previousValue: $"{prevName} ({prevDate:yyyy-MM-dd})",
            newValue: $"{existing.Name} ({existing.Date:yyyy-MM-dd})");
        return existing;
    }

    public async Task<bool> DeleteAsync(int orgId, int id, string? actorEmail)
    {
        var h = await _db.LeaveHolidays.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == id);
        if (h == null) return false;
        _db.LeaveHolidays.Remove(h);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.UserDeleted, actorEmail,
            details: $"Deleted holiday '{h.Name}' ({h.Date:yyyy-MM-dd})",
            previousValue: h.Name);
        return true;
    }

    /// <summary>True if the given date is a holiday for this org.</summary>
    public Task<bool> IsHolidayAsync(int orgId, DateTime date) =>
        _db.LeaveHolidays.AnyAsync(h => h.OrganizationId == orgId && h.Date.Date == date.Date);

    /// <summary>Get all holiday dates for an org (as a HashSet for fast lookup). Used by ShiftEngine and leave calculations.</summary>
    public async Task<HashSet<DateTime>> GetHolidayDatesAsync(int orgId, int? year = null)
    {
        var q = _db.LeaveHolidays.Where(h => h.OrganizationId == orgId);
        if (year.HasValue)
            q = q.Where(h => h.Date.Year == year.Value);
        var dates = await q.Select(h => h.Date.Date).ToListAsync();
        return dates.ToHashSet();
    }

    /// <summary>Import holidays from CSV. Columns: Name,Date(yyyy-MM-dd),Country.</summary>
    public async Task<(int imported, int skipped, List<string> errors)> ImportCsvAsync(int orgId, Stream csvStream, string? actorEmail)
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        // Read header.
        await csv.ReadAsync();
        csv.ReadHeader();

        var existingDates = (await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId)
            .Select(h => h.Date.Date)
            .ToListAsync()).ToHashSet();

        var toAdd = new List<LeaveHoliday>();
        var row = 1;
        while (await csv.ReadAsync())
        {
            row++;
            try
            {
                var name = csv.GetField<string>("Name")?.Trim();
                var dateStr = csv.GetField<string>("Date")?.Trim();
                var country = csv.GetField<string?>("Country")?.Trim() ?? "Zambia";

                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"Row {row}: Name is required.");
                    skipped++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(dateStr) || !DateTime.TryParse(dateStr, out var date))
                {
                    errors.Add($"Row {row}: Invalid date '{dateStr}'. Use yyyy-MM-dd.");
                    skipped++;
                    continue;
                }

                date = date.Date;
                if (existingDates.Contains(date))
                {
                    skipped++;
                    continue;
                }

                toAdd.Add(new LeaveHoliday
                {
                    OrganizationId = orgId,
                    Name = name,
                    Date = date,
                    Country = country,
                });
                existingDates.Add(date);
            }
            catch (Exception ex)
            {
                errors.Add($"Row {row}: {ex.Message}");
                skipped++;
            }
        }

        if (toAdd.Count > 0)
        {
            _db.LeaveHolidays.AddRange(toAdd);
            await _db.SaveChangesAsync();
            imported = toAdd.Count;
            await _audit.LogAsync(orgId, AuditAction.BulkImport, actorEmail,
                details: $"Imported {imported} holidays via CSV (skipped {skipped})");
        }

        return (imported, skipped, errors);
    }

    /// <summary>Export holidays as CSV bytes for download.</summary>
    public byte[] ExportCsv(List<LeaveHoliday> holidays)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteField("Name");
        csv.WriteField("Date");
        csv.WriteField("DayOfWeek");
        csv.WriteField("Country");
        csv.WriteField("IsRecurring");
        csv.NextRecord();

        foreach (var h in holidays.OrderBy(h => h.Date))
        {
            csv.WriteField(h.Name);
            csv.WriteField(h.Date.ToString("yyyy-MM-dd"));
            csv.WriteField(h.Date.ToString("dddd"));
            csv.WriteField(h.Country ?? "");
            csv.WriteField(h.IsRecurring ? "Yes" : "No");
            csv.NextRecord();
        }

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>Delete all holidays for an org in a given year (bulk cleanup).</summary>
    public async Task<int> DeleteYearAsync(int orgId, int year, string? actorEmail)
    {
        var holidays = await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId && h.Date.Year == year)
            .ToListAsync();
        if (holidays.Count == 0) return 0;

        _db.LeaveHolidays.RemoveRange(holidays);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.BulkImport, actorEmail,
            details: $"Deleted {holidays.Count} holidays for year {year}");
        return holidays.Count;
    }

    /// <summary>Seed the org with the Zambia public holiday calendar for the given year.</summary>
    public async Task<int> SeedZambiaHolidaysAsync(int orgId, int year, string? actorEmail)
    {
        var holidays = GetZambiaHolidays(year);
        var existingDates = (await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId && h.Date.Year == year)
            .Select(h => h.Date.Date)
            .ToListAsync()).ToHashSet();

        var toAdd = holidays
            .Where(h => !existingDates.Contains(h.Date.Date))
            .Select(h => new LeaveHoliday
            {
                OrganizationId = orgId,
                Name = h.Name,
                Date = h.Date,
                Country = "Zambia"
            })
            .ToList();

        if (toAdd.Count > 0)
        {
            _db.LeaveHolidays.AddRange(toAdd);
            await _db.SaveChangesAsync();
            await _audit.LogAsync(orgId, AuditAction.BulkImport, actorEmail,
                details: $"Seeded {toAdd.Count} Zambia public holidays for {year}");
        }
        return toAdd.Count;
    }

    /// <summary>Zambia public holidays for a given year (fixed + observance-shifted).</summary>
    public static List<(string Name, DateTime Date)> GetZambiaHolidays(int year)
    {
        var list = new List<(string Name, DateTime Date)>();

        // Fixed-date holidays (shifted to next Monday if weekend — Zambian practice).
        AddShifted(list, "New Year's Day", new DateTime(year, 1, 1));
        AddShifted(list, "Youth Day", new DateTime(year, 3, 12));
        AddShifted(list, "Labour Day", new DateTime(year, 5, 1));
        AddShifted(list, "Africa Freedom Day", new DateTime(year, 5, 25));
        AddShifted(list, "Heroes' Day", new DateTime(year, 7, 1));
        AddShifted(list, "Unity Day", new DateTime(year, 7, 2));
        AddShifted(list, "Farmers' Day", new DateTime(year, 8, 5)); // First Monday of August
        AddShifted(list, "National Prayer Day", new DateTime(year, 10, 18));
        AddShifted(list, "Independence Day", new DateTime(year, 10, 24));
        AddShifted(list, "Christmas Day", new DateTime(year, 12, 25));
        AddShifted(list, "Boxing Day", new DateTime(year, 12, 26));

        return list;
    }

    private static void AddShifted(List<(string Name, DateTime Date)> list, string name, DateTime date)
    {
        // Zambian practice: if holiday falls on Saturday, move to Monday.
        if (date.DayOfWeek == DayOfWeek.Saturday)
            date = date.AddDays(2);
        else if (date.DayOfWeek == DayOfWeek.Sunday)
            date = date.AddDays(1);
        list.Add((name, date));
    }
}
