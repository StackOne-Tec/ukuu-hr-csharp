using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

// ─────────────────────────────────────────────────────────────────────────────
// FR-001 Third-Party Device Integration
//
// Unified device model supporting 7 vendors (Hikvision, ZKTeco, Suprema,
// Dahua, Anviz, Matrix, eSSL) and 4 integration modes (REST API, SDK,
// TCP/IP, CSV file). HikvisionDevice remains as a vendor-specific model
// for backwards compatibility; the new AttendanceDevice model is the
// unified abstraction that all vendors share.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Unified attendance device configuration (FR-001).
/// Supports 7 vendors and 4 integration modes. Vendor-specific connection
/// details are stored in the ConnectionJson field (e.g. ISAPI auth for
/// Hikvision, SDK path for ZKTeco, TCP port for Suprema, etc.).
/// </summary>
public class AttendanceDevice
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Device vendor — drives which connector implementation is used.</summary>
    public DeviceVendor Vendor { get; set; } = DeviceVendor.Hikvision;

    /// <summary>Integration mode — REST API, SDK, TCP/IP, or CSV file ingestion.</summary>
    public DeviceIntegrationMode Mode { get; set; } = DeviceIntegrationMode.RestApi;

    // ───── Connection details ─────
    [MaxLength(200)]
    public string? IpAddress { get; set; }

    public int? Port { get; set; }

    [MaxLength(100)]
    public string? Username { get; set; } = "admin";

    [MaxLength(200)]
    public string? Password { get; set; }

    /// <summary>Vendor-specific connection JSON (e.g. SDK path, serial, device key).</summary>
    public string? ConnectionJson { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(200)]
    public string? DeviceSerial { get; set; }

    // ───── Sync schedule ─────
    /// <summary>True if device should be polled automatically (FR-002 auto-sync).</summary>
    public bool AutoSyncEnabled { get; set; } = true;

    /// <summary>Poll interval in minutes. 0 = use system default (5 min).</summary>
    public int SyncIntervalMinutes { get; set; } = 5;

    // ───── Status ─────
    public bool IsActive { get; set; } = true;

    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public DateTime? LastErrorAt { get; set; }
    [MaxLength(500)]
    public string? LastErrorMessage { get; set; }

    public int TotalEventsSynced { get; set; }
    public int TotalSyncErrors { get; set; }

    // ───── Meta ─────
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }
    [MaxLength(256)]
    public string? CreatedByEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ───── Helpers ─────
    public string VendorDisplay => Vendor switch
    {
        DeviceVendor.Hikvision => "Hikvision",
        DeviceVendor.ZKTeco => "ZKTeco",
        DeviceVendor.Suprema => "Suprema",
        DeviceVendor.Dahua => "Dahua",
        DeviceVendor.Anviz => "Anviz",
        DeviceVendor.Matrix => "Matrix",
        DeviceVendor.eSSL => "eSSL",
        _ => Vendor.ToString()
    };

    public string ModeDisplay => Mode switch
    {
        DeviceIntegrationMode.RestApi => "REST API",
        DeviceIntegrationMode.Sdk => "SDK",
        DeviceIntegrationMode.TcpIp => "TCP/IP",
        DeviceIntegrationMode.CsvFile => "CSV File",
        _ => Mode.ToString()
    };

    public string StatusDisplay => !IsActive ? "Disabled" :
        LastSuccessfulSyncAt.HasValue ? "Online" :
        LastErrorAt.HasValue ? "Error" : "Pending";

    public string LastSyncDisplay => LastSyncAt?.ToString("dd MMM HH:mm") ?? "Never";
    public string LastSuccessfulSyncDisplay => LastSuccessfulSyncAt?.ToString("dd MMM HH:mm") ?? "Never";

    /// <summary>Vendor brand color (used in UI chips).</summary>
    public string VendorColor => Vendor switch
    {
        DeviceVendor.Hikvision => "#D92136",  // Hikvision red
        DeviceVendor.ZKTeco => "#0066B3",      // ZKTeco blue
        DeviceVendor.Suprema => "#00A651",     // Suprema green
        DeviceVendor.Dahua => "#E2231A",       // Dahua red
        DeviceVendor.Anviz => "#0080FF",       // Anviz blue
        DeviceVendor.Matrix => "#FF6600",      // Matrix orange
        DeviceVendor.eSSL => "#6E5F92",        // eSSL purple
        _ => "#6B7280"
    };
}

/// <summary>Supported device vendors per FRS FR-001.</summary>
public enum DeviceVendor
{
    Hikvision,
    ZKTeco,
    Suprema,
    Dahua,
    Anviz,
    Matrix,
    eSSL
}

/// <summary>Integration modes supported by the platform.</summary>
public enum DeviceIntegrationMode
{
    /// <summary>HTTP REST API (e.g. Hikvision ISAPI, ZKTeco HTTP API).</summary>
    RestApi,
    /// <summary>Vendor SDK (native library, e.g. ZKTeco PullSDK, Suprema BioStar SDK).</summary>
    Sdk,
    /// <summary>TCP/IP socket protocol (e.g. Suprema BS2, Matrix COSEC).</summary>
    TcpIp,
    /// <summary>CSV file ingestion (offline devices that export punch logs as CSV).</summary>
    CsvFile
}

/// <summary>
/// A clock event from any vendor's device — normalized from vendor-specific
/// formats (Hikvision XML, ZKTeco JSON, Suprema BS2, etc.) into a single
/// canonical shape. Stored in the same table as HikvisionClockEvent for
/// backwards compatibility, but tagged with the originating vendor.
/// </summary>
public class UnifiedClockEvent
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int DeviceId { get; set; }
    public AttendanceDevice Device { get; set; } = null!;

    /// <summary>Vendor that produced this event (denormalized for fast filtering).</summary>
    public DeviceVendor Vendor { get; set; }

    [MaxLength(100)]
    public string EmployeeCode { get; set; } = string.Empty;

    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public DateTime EventTime { get; set; }
    public ClockEventType EventType { get; set; } = ClockEventType.CheckIn;

    [MaxLength(50)]
    public string? VerifyMode { get; set; } // Card / Fingerprint / Face / PIN

    [MaxLength(50)]
    public string? InOutMode { get; set; }

    [MaxLength(100)]
    public string? RawPayload { get; set; } // Original vendor payload (truncated) for audit

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    public bool IsProcessed { get; set; }
    public DateTime? ProcessedAt { get; set; }

    public string EventTimeDisplay => EventTime.ToString("dd MMM yyyy HH:mm:ss");
    public string EventTypeDisplay => EventType switch
    {
        ClockEventType.CheckIn => "Check In",
        ClockEventType.CheckOut => "Check Out",
        ClockEventType.BreakOut => "Break Out",
        ClockEventType.BreakIn => "Break In",
        _ => EventType.ToString()
    };
    public string VendorDisplay => Vendor.ToString();
}
