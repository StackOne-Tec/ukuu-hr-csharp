using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Services.Devices;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// FR-002 Automatic Device Synchronization
//
// Background service that polls every active AttendanceDevice on its own
// SyncIntervalMinutes schedule. Runs as IHostedService so it starts with
// the app and runs continuously. Uses a 60-second tick to check which
// devices are due for sync.
//
// Also handles FR-002's "automatic attendance synchronization" — i.e. the
// system pulls device events without human intervention.
// ─────────────────────────────────────────────────────────────────────────────

public class DeviceAutoSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _env;
    private readonly ILogger<DeviceAutoSyncService> _logger;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private bool IsDevelopment => _env.IsDevelopment();

    public DeviceAutoSyncService(IServiceProvider services, IHostEnvironment env, ILogger<DeviceAutoSyncService> logger)
    {
        _services = services;
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeviceAutoSyncService started — checking every {Seconds}s for devices due for sync",
            TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "DeviceAutoSyncService tick failed — will retry in {Seconds}s", TickInterval.TotalSeconds);
            }
            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<DeviceSyncOrchestrator>();

        // Find all devices that are due for sync.
        // "Due" = AutoSyncEnabled AND IsActive AND (LastSyncAt is null OR LastSyncAt + SyncIntervalMinutes <= now)
        var now = DateTime.UtcNow;
        var dueDevices = await db.AttendanceDevices
            .Where(d => d.IsActive && d.AutoSyncEnabled)
            .Where(d => d.LastSyncAt == null || d.LastSyncAt.Value.AddMinutes(d.SyncIntervalMinutes) <= now)
            .Select(d => new { d.OrganizationId, d.Id, d.Name })
            .ToListAsync(ct);

        if (dueDevices.Count == 0) return;

        _logger.LogInformation("Auto-sync: {Count} device(s) due for sync", dueDevices.Count);

        foreach (var device in dueDevices)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var result = await orchestrator.SyncDeviceAsync(device.OrganizationId, device.Id, ct);
                if (result.Success)
                    _logger.LogInformation("Auto-sync OK: {DeviceName} — fetched {Fetched}, imported {Imported}, dupes {Dupes}",
                        device.Name, result.EventsFetched, result.EventsImported, result.DuplicatesSkipped);
                else
                {
                    if (IsDevelopment)
                        _logger.LogDebug("Auto-sync FAIL: {DeviceName} — {Error}", device.Name, result.ErrorMessage);
                    else
                        _logger.LogWarning("Auto-sync FAIL: {DeviceName} — {Error}", device.Name, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                if (IsDevelopment)
                    _logger.LogDebug(ex, "Auto-sync threw for device {DeviceName}", device.Name);
                else
                    _logger.LogError(ex, "Auto-sync threw for device {DeviceName}", device.Name);
            }
        }
    }
}
