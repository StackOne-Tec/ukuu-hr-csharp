using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UkuuHr.Data;
using UkuuHr.Models;
using UkuuHr.Services;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Integration tests for the device-management API added with the device edit-mode
/// feature: POST /api/devices/save (create + edit) and POST /api/devices/sync-persons/{id}.
///
/// The app boots against its default seeded SQLite database (the app reads
/// ConnectionStrings:SqlitePath during its builder phase, before WebApplicationFactory
/// callbacks run, so per-factory overrides cannot redirect it). Because P0/C-2 seeds the
/// admin password from UKUU_ADMIN_PASSWORD — random when unset — the helper below
/// deterministically resets the seeded admin's BCrypt hash before each login so the
/// demo credentials stay stable regardless of how the database was first seeded.
/// </summary>
public class DeviceApiIntegrationTests : IClassFixture<DeviceApiIntegrationTests.DeviceApiFactory>
{
    private const string AdminEmail = "admin@ukuuhr.demo";
    private const string AdminPassword = "Admin@2025";

    private readonly DeviceApiFactory _factory;

    public DeviceApiIntegrationTests(DeviceApiFactory factory) => _factory = factory;

    /// <summary>WebApplicationFactory hosting the real app with its default database.</summary>
    public sealed class DeviceApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Non-production so the auth cookie keeps the ASP.NET Core default SecurePolicy
            // (SameAsRequest) — with CookieSecurePolicy.Always the test server's plain-HTTP
            // client would refuse to replay the cookie on subsequent requests.
            builder.UseEnvironment("Development");
        }
    }

    // ─── Test helpers ───

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private async Task<T> WithDbAsync<T>(Func<UkuuHrDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();
        return await action(db);
    }

    private async Task<int> SeedDeviceAsync(AttendanceDevice device) =>
        await WithDbAsync(async db =>
        {
            var org = await db.Organizations.FirstAsync();
            device.OrganizationId = org.Id;
            db.AttendanceDevices.Add(device);
            await db.SaveChangesAsync();
            return device.Id;
        });

    private static FormUrlEncodedContent DeviceForm(
        string name,
        string? id = null,
        string vendor = "Hikvision",
        string mode = "RestApi",
        string? ipAddress = "10.0.0.99",
        string? port = null,
        bool useHttps = false,
        string? username = "admin",
        string? password = "secret",
        bool autoSyncEnabled = true,
        string? csvFilePath = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Vendor"] = vendor,
            ["Mode"] = mode,
            ["IpAddress"] = ipAddress ?? "",
            ["Username"] = username ?? "",
            ["Password"] = password ?? "",
            ["AutoSyncEnabled"] = autoSyncEnabled ? "true" : "false",
            ["SyncIntervalMinutes"] = "5"
        };
        if (id != null) fields["Id"] = id;
        if (port != null) fields["Port"] = port;
        if (useHttps) fields["UseHttps"] = "true";
        if (csvFilePath != null) fields["CsvFilePath"] = csvFilePath;
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>
    /// Deterministically repair the seeded admin credential. P0/C-2 seeds the admin
    /// password from UKUU_ADMIN_PASSWORD (random when unset), so whatever password the
    /// database was originally seeded with is unknown here. Signing in goes through
    /// BCrypt verification of the stored hash (Phase 13.5), so overwriting the hash with
    /// a BCrypt hash of the known demo password makes login succeed on any DB state.
    /// </summary>
    private async Task EnsureAdminPasswordAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();

        var admin = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == AdminEmail);
        if (admin == null) return;

        var hash = admin.AuthUid ?? "";
        var matches = hash.StartsWith("$2") && BCrypt.Net.BCrypt.Verify(AdminPassword, hash);
        if (!matches)
        {
            admin.AuthUid = AuthService.HashPassword(AdminPassword);
            await db.SaveChangesAsync();
        }
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        await EnsureAdminPasswordAsync();

        var client = CreateClient();
        var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FormData.Email"] = AdminEmail,
            ["FormData.Password"] = AdminPassword
        }));

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.NotNull(login.Headers.Location);
        Assert.Contains("/dashboard", login.Headers.Location!.OriginalString);
        return client; // the auth cookie is attached for subsequent requests
    }

    // ─── POST /api/devices/save — create ───

    [Fact]
    public async Task Devices_Save_Creates_Device()
    {
        var client = CreateClient();
        var name = $"Test Device {Guid.NewGuid():N}";

        var resp = await client.PostAsync("/api/devices/save", DeviceForm(name, port: "8080"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/devices?saved=1", resp.Headers.Location!.OriginalString);

        var created = await WithDbAsync(db => db.AttendanceDevices.FirstOrDefaultAsync(d => d.Name == name));
        Assert.NotNull(created);
        Assert.Equal(DeviceVendor.Hikvision, created!.Vendor);
        Assert.Equal(DeviceIntegrationMode.RestApi, created.Mode);
        Assert.Equal("10.0.0.99", created.IpAddress);
        Assert.Equal(8080, created.Port);
        Assert.Equal("admin", created.Username);
        Assert.True(created.IsActive);
        Assert.False(created.UseHttps);
        Assert.Equal(5, created.SyncIntervalMinutes);
    }

    [Fact]
    public async Task Devices_Save_UseHttps_Defaults_Port_To_443()
    {
        var client = CreateClient();
        var name = $"HTTPS Device {Guid.NewGuid():N}";

        var resp = await client.PostAsync("/api/devices/save", DeviceForm(name, useHttps: true));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var created = await WithDbAsync(db => db.AttendanceDevices.FirstOrDefaultAsync(d => d.Name == name));
        Assert.NotNull(created);
        Assert.True(created!.UseHttps);
        Assert.Equal(443, created.Port);
    }

    [Fact]
    public async Task Devices_Save_Csv_Mode_Stores_FilePath_In_ConnectionJson()
    {
        var client = CreateClient();
        var name = $"CSV Device {Guid.NewGuid():N}";

        var resp = await client.PostAsync("/api/devices/save",
            DeviceForm(name, mode: "CsvFile", ipAddress: null, csvFilePath: "/data/punches.csv"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var created = await WithDbAsync(db => db.AttendanceDevices.FirstOrDefaultAsync(d => d.Name == name));
        Assert.NotNull(created);
        Assert.Equal(DeviceIntegrationMode.CsvFile, created!.Mode);
        Assert.NotNull(created.ConnectionJson);
        Assert.Contains("filePath", created.ConnectionJson);
        Assert.Contains("/data/punches.csv", created.ConnectionJson);
    }

    // ─── POST /api/devices/save — edit mode ───

    [Fact]
    public async Task Devices_Save_Edit_Updates_Fields_And_Preserves_IsActive()
    {
        var deviceId = await SeedDeviceAsync(new AttendanceDevice
        {
            Name = $"Seed Device {Guid.NewGuid():N}",
            Vendor = DeviceVendor.Hikvision,
            Mode = DeviceIntegrationMode.RestApi,
            IpAddress = "10.0.0.1",
            Port = 80,
            IsActive = false, // disabled device — editing must not re-enable it
            CreatedAt = DateTime.UtcNow,
            CreatedByEmail = AdminEmail
        });

        var client = CreateClient();
        var newName = $"Renamed Device {Guid.NewGuid():N}";
        var resp = await client.PostAsync("/api/devices/save",
            DeviceForm(newName, id: deviceId.ToString(), vendor: "ZKTeco", ipAddress: "10.1.2.3", port: "8081"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var updated = await WithDbAsync(db => db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == deviceId));
        Assert.NotNull(updated);
        Assert.Equal(newName, updated!.Name);
        Assert.Equal(DeviceVendor.ZKTeco, updated.Vendor);
        Assert.Equal("10.1.2.3", updated.IpAddress);
        Assert.Equal(8081, updated.Port);
        Assert.False(updated.IsActive); // preserved — edit must not re-enable a disabled device
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task Devices_Save_Edit_Unknown_Device_Returns_NotFound()
    {
        var client = CreateClient();
        var resp = await client.PostAsync("/api/devices/save", DeviceForm("Ghost Device", id: "999999"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ─── POST /api/devices/sync-persons/{id} — push employees to a Hikvision device ───

    [Fact]
    public async Task Devices_Sync_Persons_Requires_Authentication()
    {
        var client = CreateClient();
        var resp = await client.PostAsync("/api/devices/sync-persons/1",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        // Cookie auth challenges unauthenticated callers by redirecting to the configured
        // login page. LoginPath is "/landing" (see AddCookie in Program.cs), not "/login".
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/landing", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Devices_Sync_Persons_Device_Not_Found_Redirects_With_Error()
    {
        var client = await AuthenticatedClientAsync();
        var resp = await client.PostAsync("/api/devices/sync-persons/999999",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = Uri.UnescapeDataString(resp.Headers.Location!.OriginalString);
        Assert.Contains("pushed=0", location);
        Assert.Contains("Hikvision device not found.", location);
    }

    [Fact]
    public async Task Devices_Sync_Persons_Rejects_NonHikvision_Device()
    {
        var deviceId = await SeedDeviceAsync(new AttendanceDevice
        {
            Name = $"ZKTeco Device {Guid.NewGuid():N}",
            Vendor = DeviceVendor.ZKTeco,
            Mode = DeviceIntegrationMode.RestApi,
            IpAddress = "10.0.0.5",
            Port = 80,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        var client = await AuthenticatedClientAsync();
        var resp = await client.PostAsync($"/api/devices/sync-persons/{deviceId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = Uri.UnescapeDataString(resp.Headers.Location!.OriginalString);
        Assert.Contains("pushed=0", location);
        Assert.Contains("Hikvision device not found.", location);
    }

    [Fact]
    public async Task Devices_Sync_Persons_Unreachable_Device_Returns_Summary()
    {
        // 127.0.0.1:1 refuses connections immediately, so the ISAPI push fails fast
        // per employee — the endpoint must still report the summary via pushed=1.
        var deviceId = await SeedDeviceAsync(new AttendanceDevice
        {
            Name = $"Hikvision Unreachable {Guid.NewGuid():N}",
            Vendor = DeviceVendor.Hikvision,
            Mode = DeviceIntegrationMode.RestApi,
            IpAddress = "127.0.0.1",
            Port = 1,
            Username = "admin",
            Password = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        var client = await AuthenticatedClientAsync();
        var resp = await client.PostAsync($"/api/devices/sync-persons/{deviceId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = Uri.UnescapeDataString(resp.Headers.Location!.OriginalString);
        Assert.Contains("pushed=1", location);
        Assert.Contains("ok=0", location);
        Assert.Contains("fail=", location);
    }
}
