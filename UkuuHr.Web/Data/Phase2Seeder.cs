using Microsoft.EntityFrameworkCore;
using UkuuHr.Models;
using UkuuHr.Services;

namespace UkuuHr.Data;

/// <summary>
/// Phase 2 seeder: seeds public holidays for the org (Zambia calendar).
/// Called after Phase1Seeder completes. Idempotent.
/// </summary>
public static class Phase2Seeder
{
    public static async Task SeedAsync(UkuuHrDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        if (!await db.Organizations.AnyAsync()) return;

        var org = await db.Organizations.FirstAsync();

        // Seed current year + next year if both empty.
        if (!await db.LeaveHolidays.AnyAsync(h => h.OrganizationId == org.Id))
        {
            var holidays = new List<LeaveHoliday>();
            foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
            {
                foreach (var (name, date) in HolidayService.GetZambiaHolidays(year))
                {
                    holidays.Add(new LeaveHoliday
                    {
                        OrganizationId = org.Id,
                        Name = name,
                        Date = date,
                        Country = "Zambia"
                    });
                }
            }
            db.LeaveHolidays.AddRange(holidays);
            await db.SaveChangesAsync();
        }
    }
}
