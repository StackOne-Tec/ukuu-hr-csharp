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
            },
            new()
            {
                OrganizationId = org.Id,
                Name = "Back Door — ZKTeco",
                Vendor = DeviceVendor.ZKTeco,
                Mode = DeviceIntegrationMode.RestApi,
                IpAddress = "192.168.1.101",
                Port = 80,
                Username = "admin",
                Password = "demoPassword123",
                DeviceSerial = "ZK-TC10-20250002",
                Location = "Back entrance — receiving dock",
                AutoSyncEnabled = true,
                SyncIntervalMinutes = 10,
                IsActive = true,
                LastSuccessfulSyncAt = DateTime.UtcNow.AddMinutes(-25),
                LastSyncAt = DateTime.UtcNow.AddMinutes(-25),
                TotalEventsSynced = 532,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-25)
            },
            new()
            {
                OrganizationId = org.Id,
                Name = "Server Room — Suprema",
                Vendor = DeviceVendor.Suprema,
                Mode = DeviceIntegrationMode.RestApi,
                IpAddress = "192.168.1.102",
                Port = 80,
                Username = "admin",
                Password = "BioStar2025",
                DeviceSerial = "BS3-20250003",
                Location = "Server room — restricted access",
                AutoSyncEnabled = true,
                SyncIntervalMinutes = 15,
                IsActive = true,
                LastSuccessfulSyncAt = DateTime.UtcNow.AddHours(-2),
                LastSyncAt = DateTime.UtcNow.AddHours(-2),
                TotalEventsSynced = 89,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-20)
            },
            new()
            {
                OrganizationId = org.Id,
                Name = "Warehouse — Dahua",
                Vendor = DeviceVendor.Dahua,
                Mode = DeviceIntegrationMode.RestApi,
                IpAddress = "192.168.1.103",
                Port = 80,
                Username = "admin",
                Password = "DahuaAdmin2025",
                DeviceSerial = "DH-ASA-20250004",
                Location = "Warehouse — loading bay",
                AutoSyncEnabled = false,
                IsActive = true,
                LastSyncAt = DateTime.UtcNow.AddDays(-1),
                LastErrorAt = DateTime.UtcNow.AddDays(-1),
                LastErrorMessage = "Connection timeout — device may be offline",
                TotalEventsSynced = 0,
                TotalSyncErrors = 3,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-15)
            },
            new()
            {
                OrganizationId = org.Id,
                Name = "Office Floor 2 — Anviz",
                Vendor = DeviceVendor.Anviz,
                Mode = DeviceIntegrationMode.RestApi,
                IpAddress = null,
                Port = null,
                Username = null,
                Password = "anviz-api-key-2025",
                DeviceSerial = "ANV-C5-20250005",
                Location = "Office floor 2 — main entrance",
                AutoSyncEnabled = true,
                SyncIntervalMinutes = 30,
                IsActive = true,
                LastSuccessfulSyncAt = DateTime.UtcNow.AddMinutes(-45),
                LastSyncAt = DateTime.UtcNow.AddMinutes(-45),
                TotalEventsSynced = 215,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            },
            new()
            {
                OrganizationId = org.Id,
                Name = "Parking Gate — Matrix",
                Vendor = DeviceVendor.Matrix,
                Mode = DeviceIntegrationMode.RestApi,
                IpAddress = "192.168.1.104",
                Port = 8080,
                Username = "admin",
                Password = "COSECadmin2025",
                DeviceSerial = "COSEC-MX-20250006",
                Location = "Parking gate — vehicle entry",
                AutoSyncEnabled = false,
                IsActive = true,
                LastSyncAt = DateTime.UtcNow.AddDays(-3),
                LastSuccessfulSyncAt = DateTime.UtcNow.AddDays(-3),
                TotalEventsSynced = 412,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-8)
            },
            new()
            {
                OrganizationId = org.Id,
                Name = "Visitor Kiosk — eSSL",
                Vendor = DeviceVendor.eSSL,
                Mode = DeviceIntegrationMode.CsvFile,
                IpAddress = null,
                Port = null,
                Username = null,
                Password = null,
                DeviceSerial = "ESSL-X990-20250007",
                Location = "Visitor lobby — kiosk",
                AutoSyncEnabled = false,
                IsActive = true,
                ConnectionJson = "{\"filePath\":\"/var/data/essl-export.csv\"}",
                LastSyncAt = DateTime.UtcNow.AddHours(-6),
                LastSuccessfulSyncAt = DateTime.UtcNow.AddHours(-6),
                TotalEventsSynced = 78,
                CreatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow.AddDays(-5)
            }
        };

        db.AttendanceDevices.AddRange(devices);
        await db.SaveChangesAsync();
    }
}
