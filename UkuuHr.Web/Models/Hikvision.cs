using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>
/// Hikvision time & attendance device configuration.
/// Stores connection details for one or more Hikvision DS-K1T804 series devices.
/// </summary>
public class HikvisionDevice
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string IpAddress { get; set; } = string.Empty;

    public int Port { get; set; } = 80;

    [MaxLength(100)]
    public string Username { get; set; } = "admin";

    [MaxLength(200)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? DeviceSerial { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public int TotalEventsSynced { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string StatusDisplay => IsActive ? "Active" : "Disabled";
    public string LastSyncDisplay => LastSyncAt?.ToString("dd MMM yyyy HH:mm") ?? "Never";
    public string LastSuccessfulSyncDisplay => LastSuccessfulSyncAt?.ToString("dd MMM yyyy HH:mm") ?? "Never";
}

/// <summary>
/// Raw clock event from a Hikvision device — one record per employee per punch.
/// Mapped to Attendance records by the HikvisionSyncService.
/// </summary>
public class HikvisionClockEvent
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int DeviceId { get; set; }
    public HikvisionDevice Device { get; set; } = null!;

    [MaxLength(100)]
    public string EmployeeCode { get; set; } = string.Empty;

    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public DateTime EventTime { get; set; }
    public ClockEventType EventType { get; set; } = ClockEventType.CheckIn;

    [MaxLength(50)]
    public string? VerifyMode { get; set; }

    [MaxLength(50)]
    public string? InOutMode { get; set; }

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
}

public enum ClockEventType
{
    CheckIn,
    CheckOut,
    BreakOut,
    BreakIn
}

/// <summary>
/// Overtime request or auto-calculated record.
/// Supports multiple rate types: standard, rest-day, holiday, double-time.
/// </summary>
public class OvertimeRecord
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string EmployeeName { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>Start time of overtime period.</summary>
    public DateTime StartTime { get; set; }

    /// <summary>End time of overtime period.</summary>
    public DateTime EndTime { get; set; }

    /// <summary>Calculated overtime hours (EndTime - StartTime minus breaks).</summary>
    public double Hours { get; set; }

    public OvertimeRateType RateType { get; set; } = OvertimeRateType.Standard;

    /// <summary>Multiplier applied to base hourly rate (1.5, 2.0, etc.).</summary>
    public double RateMultiplier { get; set; } = 1.5;

    /// <summary>Employee's hourly rate at time of calculation.</summary>
    public double HourlyRate { get; set; }

    /// <summary>Calculated pay = Hours × HourlyRate × RateMultiplier.</summary>
    public double Pay => Hours * HourlyRate * RateMultiplier;

    public OvertimeSource Source { get; set; } = OvertimeSource.Manual;

    public string? Reason { get; set; }

    public OvertimeStatus Status { get; set; } = OvertimeStatus.Pending;

    [MaxLength(450)]
    public string? RequestedByUserId { get; set; }
    [MaxLength(450)]
    public string? ApprovedByUserId { get; set; }
    [MaxLength(256)]
    public string? ApprovedByEmail { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApproverNotes { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string RateTypeDisplay => RateType switch
    {
        OvertimeRateType.Standard => "Standard (1.5×)",
        OvertimeRateType.RestDay => "Rest Day (2.0×)",
        OvertimeRateType.PublicHoliday => "Public Holiday (2.5×)",
        OvertimeRateType.DoubleTime => "Double Time (2.0×)",
        _ => RateType.ToString()
    };

    public string StatusDisplay => Status switch
    {
        OvertimeStatus.Pending => "Pending",
        OvertimeStatus.Approved => "Approved",
        OvertimeStatus.Rejected => "Rejected",
        OvertimeStatus.AutoApproved => "Auto-Approved",
        _ => Status.ToString()
    };

    public string DateDisplay => Date.ToString("dd MMM yyyy");
    public string TimeWindow => $"{StartTime:HH:mm} – {EndTime:HH:mm}";
}

public enum OvertimeRateType
{
    Standard,       // 1.5× — regular weekday overtime
    RestDay,        // 2.0× — weekend/rest day
    PublicHoliday,  // 2.5× — public holiday
    DoubleTime      // 2.0× — double-time threshold
}

public enum OvertimeSource
{
    Manual,         // Employee/manager entered manually
    AutoCalculated, // System computed from attendance clock events
    Hikvision       // Derived from Hikvision device clock events
}

public enum OvertimeStatus
{
    Pending,
    Approved,
    Rejected,
    AutoApproved
}

/// <summary>
/// Leave balance per employee per leave type per year.
/// Tracks entitlement, used days, carried-forward, and remaining.
/// </summary>
public class LeaveBalance
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public int LeaveTypeId { get; set; }
    public LeaveType LeaveType { get; set; } = null!;

    public int Year { get; set; }

    public double EntitlementDays { get; set; }
    public double UsedDays { get; set; }
    public double CarriedForwardDays { get; set; }
    public double AdjustedDays { get; set; }

    public double RemainingDays => EntitlementDays + CarriedForwardDays + AdjustedDays - UsedDays;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
