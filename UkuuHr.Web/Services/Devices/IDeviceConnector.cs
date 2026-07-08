using UkuuHr.Models;

namespace UkuuHr.Services.Devices;

// ─────────────────────────────────────────────────────────────────────────────
// FR-001 Vendor Connector Abstraction
//
// IDeviceConnector is the contract that every vendor integration implements.
// The DeviceSyncOrchestrator picks the right connector by inspecting the
// device's Vendor + Mode fields and delegates to it.
//
// Each connector returns NormalizedClockEvents — vendor-specific JSON / XML /
// binary / CSV formats are translated into a single canonical shape.
//
// SDK + TCP connectors are stubbed with clear "requires vendor SDK" exceptions
// because they need native binaries that can't be bundled here. The REST +
// CSV connectors are fully functional against real vendor APIs.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A single clock event in vendor-neutral form, ready to persist.</summary>
public sealed record NormalizedClockEvent(
    string EmployeeCode,
    DateTime EventTime,
    ClockEventType EventType,
    string? VerifyMode,
    string? InOutMode,
    string? RawPayload);

/// <summary>Result of a sync operation.</summary>
public sealed record DeviceSyncResult(
    bool Success,
    int EventsFetched,
    int EventsImported,
    int DuplicatesSkipped,
    string? ErrorMessage,
    TimeSpan Duration)
{
    public static DeviceSyncResult Ok(int fetched, int imported, int dupes, TimeSpan duration) =>
        new(true, fetched, imported, dupes, null, duration);

    public static DeviceSyncResult Fail(string error, TimeSpan duration) =>
        new(false, 0, 0, 0, error, duration);
}

/// <summary>
/// Connector contract for a vendor + mode combination. Implementations must be
/// thread-safe and stateless (the orchestrator may cache them as singletons).
/// </summary>
public interface IDeviceConnector
{
    /// <summary>Vendor this connector handles.</summary>
    DeviceVendor Vendor { get; }

    /// <summary>Integration mode this connector implements.</summary>
    DeviceIntegrationMode Mode { get; }

    /// <summary>Probe the device for reachability without fetching events.</summary>
    Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default);

    /// <summary>Fetch all clock events since the given timestamp. Null = fetch everything.</summary>
    Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// Connector registry — looks up the right connector for a (Vendor, Mode) pair.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Resolves the correct connector for a device configuration.</summary>
public interface IDeviceConnectorRegistry
{
    IDeviceConnector? Resolve(DeviceVendor vendor, DeviceIntegrationMode mode);
    IReadOnlyList<IDeviceConnector> All { get; }
}

/// <summary>Default in-memory registry. Connectors register themselves in constructor.</summary>
public class DeviceConnectorRegistry : IDeviceConnectorRegistry
{
    private readonly Dictionary<(DeviceVendor, DeviceIntegrationMode), IDeviceConnector> _byKey = new();

    public DeviceConnectorRegistry(IEnumerable<IDeviceConnector> connectors)
    {
        foreach (var c in connectors)
        {
            _byKey[(c.Vendor, c.Mode)] = c;
        }
    }

    public IDeviceConnector? Resolve(DeviceVendor vendor, DeviceIntegrationMode mode) =>
        _byKey.TryGetValue((vendor, mode), out var c) ? c : null;

    public IReadOnlyList<IDeviceConnector> All => _byKey.Values.ToList();
}
