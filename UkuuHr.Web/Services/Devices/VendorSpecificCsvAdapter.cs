using UkuuHr.Models;

namespace UkuuHr.Services.Devices;

/// <summary>
/// Adapter that wraps the shared CsvConnector and tags it with a specific vendor.
/// The CSV format is identical across vendors, but the registry needs (Vendor, Mode)
/// tuples — so we create one adapter per vendor for the CsvFile mode.
/// </summary>
public sealed class VendorSpecificCsvAdapter : IDeviceConnector
{
    private readonly CsvConnector _inner;
    public DeviceVendor Vendor { get; }
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.CsvFile;

    public VendorSpecificCsvAdapter(CsvConnector inner, DeviceVendor vendor)
    {
        _inner = inner;
        Vendor = vendor;
    }

    public Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
        => _inner.PingAsync(device, ct);

    public Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
        => _inner.SyncAsync(device, since, ct);
}
