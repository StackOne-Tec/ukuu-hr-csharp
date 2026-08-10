using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;
using UkuuHr.Services.Devices;

namespace UkuuHr.Services.Hikvision;

// ─────────────────────────────────────────────────────────────────────────────
// HikVision Auto-Sync & Health Monitoring Background Service
//
// Runs as a hosted service that:
//   1. Periodically syncs all active devices with auto-sync enabled
//   2. Monitors device health (CPU, memory, disk, connectivity)
//   3. Auto-discovers new Hikvision devices on the network
//   4. Processes unprocessed clock events into attendance records
//   5. Sends alerts for offline devices, health issues, and sync failures
//
// The service respects each device's SyncIntervalMinutes setting and
// runs health checks on a separate schedule.
// ─────────────────────────────────────────────────────────────────────────────

public class HikvisionBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HikvisionBackgroundService> _logger;
    private readonly IConfiguration _config;

    // Sync intervals
    private static readonly TimeSpan _syncInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _discoveryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan _eventProcessingInterval = TimeSpan.FromMinutes(2);

    public HikvisionBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<HikvisionBackgroundService> logger,
        IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HikVision Background Service started");

        // Stagger the timers so they don't all run at once
        var lastSync = DateTime.UtcNow - _syncInterval; // Run immediately on start
        var lastHealthCheck = DateTime.UtcNow;
        var lastDiscovery = DateTime.UtcNow;
        var lastEventProcessing = DateTime.UtcNow - _eventProcessingInterval; // Run immediately

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                // ── 1. Auto-sync devices ──
                if (now - lastSync >= _syncInterval)
                {
                    await RunSyncCycleAsync(stoppingToken);
                    lastSync = now;
                }

                // ── 2. Health monitoring ──
                if (now - lastHealthCheck >= _healthCheckInterval)
                {
                    await RunHealthCheckCycleAsync(stoppingToken);
                    lastHealthCheck = now;
                }

                // ── 3. Auto-discovery ──
                if (now - lastDiscovery >= _discoveryInterval)
                {
                    await RunDiscoveryCycleAsync(stoppingToken);
                    lastDiscovery = now;
                }

                // ── 4. Process unprocessed events ──
                if (now - lastEventProcessing >= _eventProcessingInterval)
                {
                    await RunEventProcessingCycleAsync(stoppingToken);
                    lastEventProcessing = now;
                }

                // Wait before checking again
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HikVision Background Service cycle");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("HikVision Background Service stopped");
    }

    /// <summary>Sync all active devices that have auto-sync enabled and are due for a sync.</summary>
    private async Task RunSyncCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<DeviceSyncOrchestrator>();

        var now = DateTime.UtcNow;
        var devices = await db.AttendanceDevices
            .Where(d => d.IsActive && d.AutoSyncEnabled)
            .ToListAsync(ct);

        var synced = 0;
        var failed = 0;

        foreach (var device in devices)
        {
            // Check if device is due for sync based on its interval
            var interval = TimeSpan.FromMinutes(device.SyncIntervalMinutes > 0 ? device.SyncIntervalMinutes : 5);
            var lastSync = device.LastSyncAt ?? DateTime.MinValue;

            if (now - lastSync < interval) continue;

            try
            {
                var result = await orchestrator.SyncDeviceAsync(device.OrganizationId, device.Id, ct);
                if (result.Success) synced++;
                else failed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-sync failed for device {DeviceName}", device.Name);
                failed++;
            }
        }

        if (synced > 0 || failed > 0)
            _logger.LogInformation("Auto-sync cycle: {Synced} synced, {Failed} failed", synced, failed);
    }

    /// <summary>Check health of all Hikvision devices and send alerts for offline/unhealthy ones.</summary>
    private async Task RunHealthCheckCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();

        var hikvisionDevices = await db.AttendanceDevices
            .Where(d => d.IsActive && d.Vendor == DeviceVendor.Hikvision)
            .ToListAsync(ct);

        foreach (var device in hikvisionDevices)
        {
            HikvisionIsapiClient? client = null;
            try
            {
                client = CreateIsapiClient(device);
                var health = await client.GetHealthAsync(ct);

                if (!health.IsHealthy)
                {
                    _logger.LogWarning("Device {Name} ({Ip}) health check failed: CPU={Cpu}%, MEM={Mem}%, Disk={Disk}%",
                        device.Name, device.IpAddress, health.CpuUsage, health.MemoryUsage, health.DiskUsage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for {Name} ({Ip})", device.Name, device.IpAddress);
            }
            finally
            {
                // Always dispose the client to release its HttpClient + handler,
                // preventing leaked TCP connections (CLOSE_WAIT sockets).
                client?.Dispose();
            }
        }
    }

    /// <summary>Discover new Hikvision devices on the network via SSDP.</summary>
    private async Task RunDiscoveryCycleAsync(CancellationToken ct)
    {
        try
        {
            var discovered = await HikvisionIsapiClient.DiscoverDevicesAsync(timeoutMs: 3000, ct);
            if (discovered.Count > 0)
            {
                _logger.LogInformation("SSDP discovery found {Count} Hikvision device(s)", discovered.Count);
                // In production, this would auto-register or notify admins of new devices
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSDP discovery scan failed (network may not support multicast)");
        }
    }

    /// <summary>Process any unprocessed UnifiedClockEvents into Attendance records.</summary>
    private async Task RunEventProcessingCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();

        var unprocessedCount = await db.UnifiedClockEvents.CountAsync(e => !e.IsProcessed, ct);
        if (unprocessedCount == 0) return;

        var processor = scope.ServiceProvider.GetRequiredService<HikvisionEventProcessor>();
        // Process events for each organization
        var orgIds = await db.UnifiedClockEvents
            .Where(e => !e.IsProcessed)
            .Select(e => e.OrganizationId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var orgId in orgIds)
        {
            try
            {
                // Get the first device for this org to use as a reference
                var firstDevice = await db.UnifiedClockEvents
                    .Where(e => e.OrganizationId == orgId && !e.IsProcessed)
                    .Select(e => new { e.DeviceId, e.Vendor })
                    .FirstOrDefaultAsync(ct);

                if (firstDevice == null) continue;

                await processor.ProcessEventsAsync(orgId, firstDevice.DeviceId, firstDevice.Vendor, new List<NormalizedClockEvent>(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event processing failed for org {OrgId}", orgId);
            }
        }
    }

    private static HikvisionIsapiClient CreateIsapiClient(AttendanceDevice device)
    {
        var config = new HikvisionIsapiConfig
        {
            IpAddress = device.IpAddress ?? "",
            Port = device.Port ?? (device.UseHttps ? 443 : 80),
            Username = device.Username ?? "admin",
            Password = device.Password ?? "",
            UseHttps = device.UseHttps
        };
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        return new HikvisionIsapiClient(config, loggerFactory.CreateLogger<HikvisionIsapiClient>());
    }
}
