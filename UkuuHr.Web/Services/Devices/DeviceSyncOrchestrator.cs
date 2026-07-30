using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;
using UkuuHr.Services.Hikvision;

namespace UkuuHr.Services.Devices;

// ─────────────────────────────────────────────────────────────────────────────
// FR-001 + FR-002 Orchestrator
//
// Picks the right connector for each device, runs the sync, persists events,
// detects duplicates, and updates the device's sync metadata. This is the
// single entry point for "sync this device now" — called by the UI button
// and by the auto-sync background service.
//
// Updated: Now properly persists events from REST connectors by extracting
// NormalizedClockEvents from the DeviceSyncResultWithEvents response.
// ─────────────────────────────────────────────────────────────────────────────

public class DeviceSyncOrchestrator
{
    private readonly UkuuHrDbContext _db;
    private readonly IDeviceConnectorRegistry _registry;
    private readonly ILogger<DeviceSyncOrchestrator> _logger;
    private readonly HikvisionEventProcessor _eventProcessor;

    public DeviceSyncOrchestrator(
        UkuuHrDbContext db,
        IDeviceConnectorRegistry registry,
        ILogger<DeviceSyncOrchestrator> logger,
        HikvisionEventProcessor eventProcessor)
    {
        _db = db;
        _registry = registry;
        _logger = logger;
        _eventProcessor = eventProcessor;
    }

    /// <summary>Sync a single device. Returns the sync result.</summary>
    public async Task<DeviceSyncResult> SyncDeviceAsync(int orgId, int deviceId, CancellationToken ct = default)
    {
        var device = await _db.AttendanceDevices.FirstOrDefaultAsync(d => d.OrganizationId == orgId && d.Id == deviceId, ct);
        if (device == null) return DeviceSyncResult.Fail("Device not found", TimeSpan.Zero);
        if (!device.IsActive) return DeviceSyncResult.Fail("Device is disabled", TimeSpan.Zero);

        var connector = _registry.Resolve(device.Vendor, device.Mode);
        if (connector == null)
        {
            var msg = $"No connector registered for vendor={device.Vendor}, mode={device.Mode}";
            device.LastErrorAt = DateTime.UtcNow;
            device.LastErrorMessage = msg;
            device.TotalSyncErrors++;
            await _db.SaveChangesAsync(ct);
            return DeviceSyncResult.Fail(msg, TimeSpan.Zero);
        }

        _logger.LogInformation("Syncing device {DeviceName} ({Vendor}/{Mode})", device.Name, device.Vendor, device.Mode);
        var since = device.LastSuccessfulSyncAt;

        // Check if the connector supports returning events directly
        DeviceSyncResult result;
        List<NormalizedClockEvent>? events = null;

        if (connector is IDeviceConnectorWithEvents connectorWithEvents)
        {
            var resultWithEvents = await connectorWithEvents.SyncWithEventsAsync(device, since, ct);
            events = resultWithEvents.Events;
            result = resultWithEvents.ToDeviceSyncResult();
        }
        else
        {
            result = await connector.SyncAsync(device, since, ct);
        }

        device.LastSyncAt = DateTime.UtcNow;
        if (result.Success)
        {
            device.LastSuccessfulSyncAt = DateTime.UtcNow;
            device.LastErrorAt = null;
            device.LastErrorMessage = null;

            // Persist the events from the connector.
            var (imported, dupes) = await PersistEventsAsync(orgId, device, result, events, ct);
            device.TotalEventsSynced += imported;

            return DeviceSyncResult.Ok(result.EventsFetched, imported, dupes, result.Duration);
        }
        else
        {
            device.LastErrorAt = DateTime.UtcNow;
            device.LastErrorMessage = result.ErrorMessage;
            device.TotalSyncErrors++;
            await _db.SaveChangesAsync(ct);
            return result;
        }
    }

    /// <summary>Persist fetched events into UnifiedClockEvent table, then process into Attendance records.</summary>
    private async Task<(int imported, int duplicates)> PersistEventsAsync(
        int orgId, AttendanceDevice device, DeviceSyncResult result, List<NormalizedClockEvent>? events, CancellationToken ct)
    {
        if (events == null || events.Count == 0)
        {
            // No events to persist — just save device metadata
            await _db.SaveChangesAsync(ct);
            return (0, 0);
        }

        // Use the event processor to persist and process
        var processingResult = await _eventProcessor.ProcessEventsAsync(orgId, device.Id, device.Vendor, events, ct);

        _logger.LogInformation(
            "Persisted {Imported} events from {DeviceName} ({Dupes} duplicates, {Unmatched} unmatched, {Attendance} attendance records)",
            processingResult.EventsImported, device.Name, processingResult.DuplicatesSkipped,
            processingResult.UnmatchedEmployees, processingResult.AttendanceRecordsCreated + processingResult.AttendanceRecordsUpdated);

        return (processingResult.EventsImported, processingResult.DuplicatesSkipped);
    }

    /// <summary>Import a CSV file as a one-off (no device config required).</summary>
    public async Task<DeviceSyncResult> ImportCsvAsync(int orgId, int deviceId, Stream csvStream, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        var device = await _db.AttendanceDevices.FirstOrDefaultAsync(d => d.OrganizationId == orgId && d.Id == deviceId, ct);
        if (device == null) return DeviceSyncResult.Fail("Device not found", TimeSpan.Zero);

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"ukuuhr-import-{Guid.NewGuid():N}.csv");
            using (var fs = File.Create(tempPath))
                await csvStream.CopyToAsync(fs, ct);

            var events = CsvConnector.ParseCsv(tempPath, null);
            File.Delete(tempPath);

            // Use the event processor to persist
            var processingResult = await _eventProcessor.ProcessEventsAsync(orgId, device.Id, device.Vendor, events, ct);

            device.LastSyncAt = DateTime.UtcNow;
            device.LastSuccessfulSyncAt = DateTime.UtcNow;
            device.TotalEventsSynced += processingResult.EventsImported;
            await _db.SaveChangesAsync(ct);

            return DeviceSyncResult.Ok(events.Count, processingResult.EventsImported, processingResult.DuplicatesSkipped, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            device.LastErrorAt = DateTime.UtcNow;
            device.LastErrorMessage = ex.Message;
            device.TotalSyncErrors++;
            await _db.SaveChangesAsync(ct);
            return DeviceSyncResult.Fail($"CSV import error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    /// <summary>Sync all active devices in the org (used by the auto-sync background service).</summary>
    public async Task<List<DeviceSyncResult>> SyncAllDevicesAsync(int orgId, CancellationToken ct = default)
    {
        var deviceIds = await _db.AttendanceDevices
            .Where(d => d.OrganizationId == orgId && d.IsActive && d.AutoSyncEnabled)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var results = new List<DeviceSyncResult>();
        foreach (var id in deviceIds)
        {
            var result = await SyncDeviceAsync(orgId, id, ct);
            results.Add(result);
        }
        return results;
    }

    /// <summary>Ping a device without syncing (used by the UI "Test connection" button).</summary>
    public async Task<(bool reachable, string? error)> PingAsync(int orgId, int deviceId)
    {
        var device = await _db.AttendanceDevices.FirstOrDefaultAsync(d => d.OrganizationId == orgId && d.Id == deviceId);
        if (device == null) return (false, "Device not found");

        var connector = _registry.Resolve(device.Vendor, device.Mode);
        if (connector == null) return (false, $"No connector for {device.Vendor}/{device.Mode}");

        return await connector.PingAsync(device);
    }
}
