using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>Attendance record per employee per day.</summary>
public class Attendance
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string EmployeeName { get; set; } = string.Empty;

    /// <summary>yyyy-MM-dd — for fast filtering.</summary>
    public string DateKey { get; set; } = string.Empty;

    public DateTime Date { get; set; }
    public DateTime? CheckIn { get; set; }
    public DateTime? CheckOut { get; set; }
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
    public AttendanceSource Source { get; set; } = AttendanceSource.Manual;
    public string? Notes { get; set; }

    /// <summary>Break duration in minutes.</summary>
    public int BreakMinutes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string CheckInLabel => CheckIn?.ToString("hh:mm tt") ?? "—";
    public string CheckOutLabel => CheckOut?.ToString("hh:mm tt") ?? "—";

    public double WorkedHours
    {
        get
        {
            if (CheckIn is null || CheckOut is null) return 0;
            return (CheckOut.Value - CheckIn.Value).TotalHours - (BreakMinutes / 60.0);
        }
    }
}

public enum AttendanceStatus
{
    Present,
    Absent,
    Remote,
    Late,
    OnLeave,
    HalfDay
}

public enum AttendanceSource
{
    Manual,
    Clock,
    Import,
    System
}

/// <summary>Leave request created by an employee.</summary>
public class LeaveRequest
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string EmployeeName { get; set; } = string.Empty;

    public int LeaveTypeId { get; set; }
    public LeaveType LeaveType { get; set; } = null!;

    [MaxLength(100)]
    public string LeaveTypeName { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;

    public bool IsExceptional { get; set; }
    public int? DeductibleDays { get; set; }
    public int? HolidayDays { get; set; }

    public int RequestedDays => CalculateBusinessDays(StartDate, EndDate);

    [MaxLength(450)]
    public string? RequestedByUserId { get; set; }
    [MaxLength(450)]
    public string? ReviewedByUserId { get; set; }
    [MaxLength(450)]
    public string? ReviewedByEmail { get; set; }
    public string? RejectionReason { get; set; }
    public string? ApproverNotes { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string PeriodLabel => $"{StartDate:yyyy-MM-dd} To {EndDate:yyyy-MM-dd}";

    public static int CalculateBusinessDays(DateTime start, DateTime end)
    {
        if (end < start) return 0;
        var days = 0;
        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                days++;
        }
        return days;
    }
}

public enum LeaveRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled
}

/// <summary>Configurable leave types per organization.</summary>
public class LeaveType
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Color { get; set; } = "#25163F";

    /// <summary>Default annual entitlement in days.</summary>
    public int DefaultDays { get; set; } = 0;

    public bool IsPaid { get; set; } = true;
    public bool RequiresApproval { get; set; } = true;
    public bool CarryForward { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Public holiday per country.</summary>
public class LeaveHoliday
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    [MaxLength(100)]
    public string? Country { get; set; }

    public string DateKey => Date.ToString("yyyy-MM-dd");
}
