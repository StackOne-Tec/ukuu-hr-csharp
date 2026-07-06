using System.Globalization;
using System.Text;
using CsvHelper;
using UkuuHr.Models;

namespace UkuuHr.Services.Devices;

// ─────────────────────────────────────────────────────────────────────────────
// FR-001 CSV Connector + SDK/TCP Stubs
//
// CsvConnector — handles CSV file ingestion for any vendor. The CSV is
// expected to have columns: EmployeeCode,EventTime,EventType,VerifyMode
// where EventType is "CheckIn" / "CheckOut" / "BreakOut" / "BreakIn".
//
// SdkConnector + TcpIpConnector — abstract base stubs. Each vendor's SDK
// requires a native binary (Windows .dll or Linux .so) that can't be
// bundled in a cross-platform NuGet. These stubs return a clear error
// message so users know exactly what's missing and can drop in the
// vendor's SDK + a concrete subclass.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// CSV file connector — works for any vendor that exports punch logs as CSV.
/// File path is read from device.ConnectionJson: { "filePath": "/path/to/export.csv" }
/// </summary>
public class CsvConnector : IDeviceConnector
{
    public DeviceVendor Vendor => default; // Vendor-agnostic — registered for all vendors.
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.CsvFile;

    public Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        var path = ExtractFilePath(device);
        if (string.IsNullOrEmpty(path))
            return Task.FromResult<(bool reachable, string? error)>((false, "CSV file path not configured (set ConnectionJson: { \"filePath\": \"...\" })"));
        return Task.FromResult<(bool reachable, string? error)>((File.Exists(path), File.Exists(path) ? null : $"File not found: {path}"));
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        var path = ExtractFilePath(device);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return DeviceSyncResult.Fail($"CSV file not found: {path}", DateTime.UtcNow - start);

        try
        {
            var events = await Task.Run(() => ParseCsv(path, since), ct);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"CSV parse error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    private static string? ExtractFilePath(AttendanceDevice device)
    {
        if (string.IsNullOrEmpty(device.ConnectionJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(device.ConnectionJson);
            return doc.RootElement.TryGetProperty("filePath", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Parse a CSV file into NormalizedClockEvent records.</summary>
    public static List<NormalizedClockEvent> ParseCsv(string path, DateTime? since)
    {
        var events = new List<NormalizedClockEvent>();
        using var reader = new StreamReader(path, Encoding.UTF8);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            var empCode = csv.GetField<string>("EmployeeCode") ?? "";
            var timeStr = csv.GetField<string>("EventTime") ?? "";
            var typeStr = csv.GetField<string?>("EventType") ?? "CheckIn";
            var verify = csv.TryGetField<string>("VerifyMode", out var v) ? v : null;

            if (!DateTime.TryParse(timeStr, out var eventTime)) continue;
            if (since.HasValue && eventTime < since.Value) continue;

            var eventType = typeStr.Trim().ToLowerInvariant() switch
            {
                "checkout" or "out" or "exit" => ClockEventType.CheckOut,
                "breakout" or "break_out" => ClockEventType.BreakOut,
                "breakin" or "break_in" => ClockEventType.BreakIn,
                _ => ClockEventType.CheckIn
            };
            events.Add(new NormalizedClockEvent(empCode, eventTime, eventType, verify, null, null));
        }
        return events;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SDK + TCP/IP stubs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Base class for vendor SDK connectors. Each vendor's SDK requires a native
/// binary that can't be bundled cross-platform. Subclass + register the
/// concrete implementation once the SDK is installed.
/// </summary>
public abstract class SdkConnectorBase : IDeviceConnector
{
    public abstract DeviceVendor Vendor { get; }
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.Sdk;

    public Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        return Task.FromResult<(bool reachable, string? error)>((false, $"{Vendor} SDK connector not registered. Install the vendor SDK and register a concrete subclass in Program.cs."));
    }

    public Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        return Task.FromResult(DeviceSyncResult.Fail(
            $"{Vendor} SDK connector not registered. Install the {Vendor} SDK (e.g. ZKTeco PullSDK, Suprema BioStar SDK) and provide a concrete implementation.",
            TimeSpan.Zero));
    }
}

/// <summary>
/// Base class for TCP/IP socket connectors. Vendors like Suprema BS2, Matrix
/// COSEC, and eSSL offer raw TCP protocols. Each vendor's protocol is
/// different, so subclasses implement the wire format.
/// </summary>
public abstract class TcpIpConnectorBase : IDeviceConnector
{
    public abstract DeviceVendor Vendor { get; }
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.TcpIp;

    public Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        return Task.FromResult<(bool reachable, string? error)>((false, $"{Vendor} TCP/IP connector not registered. Implement a subclass that speaks the vendor's wire protocol."));
    }

    public Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        return Task.FromResult(DeviceSyncResult.Fail(
            $"{Vendor} TCP/IP connector not registered. Implement a subclass that speaks the vendor's wire protocol on port {device.Port?.ToString() ?? "unknown"}.",
            TimeSpan.Zero));
    }
}

// Concrete SDK stubs for each vendor — users replace these with real impls.
public class ZKTecoSdkConnector : SdkConnectorBase { public override DeviceVendor Vendor => DeviceVendor.ZKTeco; }
public class SupremaSdkConnector : SdkConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Suprema; }
public class AnvizSdkConnector : SdkConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Anviz; }
public class MatrixSdkConnector : SdkConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Matrix; }
public class EsslSdkConnector : SdkConnectorBase { public override DeviceVendor Vendor => DeviceVendor.eSSL; }
public class DahuaSdkConnector : SdkConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Dahua; }

// Concrete TCP stubs for vendors that offer TCP protocols.
public class SupremaTcpConnector : TcpIpConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Suprema; }
public class MatrixTcpConnector : TcpIpConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Matrix; }
public class EsslTcpConnector : TcpIpConnectorBase { public override DeviceVendor Vendor => DeviceVendor.eSSL; }
public class AnvizTcpConnector : TcpIpConnectorBase { public override DeviceVendor Vendor => DeviceVendor.Anviz; }
