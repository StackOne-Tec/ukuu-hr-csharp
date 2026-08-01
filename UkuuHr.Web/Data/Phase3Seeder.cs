using Microsoft.EntityFrameworkCore;
using UkuuHr.Models;

namespace UkuuHr.Data;

/// <summary>
/// Phase 3 seeder: creates demo attendance devices covering all 7 vendors.
/// Each device gets a realistic configuration so the UI shows the full
/// vendor matrix immediately on first run.
/// </summary>
public static class Phase3Seeder
{
    public static async Task SeedAsync(UkuuHrDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        if (!await db.Organizations.AnyAsync()) return;
        if (await db.AttendanceDevices.AnyAsync()) return; // idempotent

        var org = await db.Organizations.FirstAsync();

        var devices = new List<AttendanceDevice>
        {
            new()
            {
                OrganizationId = org.Id,
                Name = "Main Entrance — Hikvision",
                Vendor = DeviceVendor.Hikvision,
                Mode = DeviceIntegrationMode.RestApi,
                IpAddress = "192.168.1.100",
                Port = 80,
                Username = "admin",
                Password = "demoPassword123",
                DeviceSerial = "DS-K1T804MF-20250001",
                Location = "Ground floor — main lobby",
                AutoSyncEnabled = true,
                SyncIntervalMinutes = 5,
                IsActive = true,
                LastSuccessfulSyncAt = DateTime.UtcNow.AddMinutes(-12),
                LastSyncAt = DateTime.UtcNow.AddMinutes(-12),
                TotalEventsSynced = 1247,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-30)
            }
            // NOTE: Other vendors (ZKTeco, Suprema, Dahua, Anviz, Matrix, eSSL) were removed
            // per user request — the app now supports Hikvision devices only.
        };

        db.AttendanceDevices.AddRange(devices);
        await db.SaveChangesAsync();
    }
}
