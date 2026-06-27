using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>
/// A payroll run for one employee for one pay period.
/// Holds the full gross-to-net computation per the country's statutory rules.
/// </summary>
public class PayrollRun
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string EmployeeName { get; set; } = string.Empty;

    /// <summary>Batch ID — null for individual runs.</summary>
    public string? BatchId { get; set; }

    public int Month { get; set; }
    public int Year { get; set; }

    public DateTime PayPeriodStart { get; set; }
    public DateTime PayPeriodEnd { get; set; }

    public PayrollStatus Status { get; set; } = PayrollStatus.Pending;

    // ───────────── Amounts ─────────────
    public double Base { get; set; }
    public double Allowances { get; set; }
    public double OvertimePay { get; set; }
    public double Bonuses { get; set; }
    public double NonTaxableAllowances { get; set; }

    public double Gross => Base + Allowances + OvertimePay + Bonuses + NonTaxableAllowances;

    public double Paye { get; set; }
    public double Napsa { get; set; }
    public double Nhima { get; set; }
    public double OtherDeductions { get; set; }
    public double TotalDeductions => Paye + Napsa + Nhima + OtherDeductions;

    public double Net => Gross - TotalDeductions;

    // ───────────── Inputs ─────────────
    public double LeaveAccumulationDays { get; set; }
    public double PayePercent { get; set; }
    public double NapsaPercent { get; set; }
    public double NhimaPercent { get; set; }
    public double OvertimeHours { get; set; }
    public double OvertimeRate { get; set; }
    public double BonusAmount { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "ZMW";

    // ───────────── Approval workflow ─────────────
    public PayrollApprovalStatus ApprovalStatus { get; set; } = PayrollApprovalStatus.Pending;
    [MaxLength(450)]
    public string? SubmittedByUserId { get; set; }
    [MaxLength(256)]
    public string? SubmittedByEmail { get; set; }
    public DateTime? SubmittedAt { get; set; }
    [MaxLength(450)]
    public string? ApprovedByUserId { get; set; }
    [MaxLength(256)]
    public string? ApprovedByEmail { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApproverNotes { get; set; }
    [MaxLength(450)]
    public string? RejectedByUserId { get; set; }
    [MaxLength(256)]
    public string? RejectedByEmail { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    // ───────────── Payslip delivery ─────────────
    public PayslipDeliveryStatus PayslipDelivery { get; set; } = PayslipDeliveryStatus.NotSent;
    [MaxLength(256)]
    public string? SentToEmail { get; set; }
    public DateTime? SentAt { get; set; }

    // ───────────── Meta ─────────────
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string PeriodLabel => $"{System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(Month)} {Year}";
}

public enum PayrollStatus
{
    Pending,
    Processed,
    PendingApproval,
    Approved,
    Rejected,
    Paid
}

public enum PayrollApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

public enum PayslipDeliveryStatus
{
    NotSent,
    Queued,
    Sent,
    Failed
}

/// <summary>Department-shift assignment for scheduling.</summary>
public class DepartmentShiftAssignment
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(150)]
    public string Department { get; set; } = string.Empty;

    public ShiftType Shift { get; set; } = ShiftType.Morning;
    public ShiftSchedule Schedule { get; set; } = ShiftSchedule.Weekdays;

    /// <summary>Optional employee-level override.</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Minutes since midnight (start).</summary>
    public int? StartMinutes { get; set; }
    /// <summary>Minutes since midnight (end).</summary>
    public int? EndMinutes { get; set; }

    /// <summary>Bitmask: bit 0 = Mon ... bit 6 = Sun.</summary>
    public int DaysOfWeekMask { get; set; } = 0b0011111; // Mon-Fri

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string ShiftDisplay => Shift switch
    {
        ShiftType.Morning => "Morning",
        ShiftType.Mid => "Mid",
        ShiftType.Night => "Night",
        _ => Shift.ToString()
    };

    public string ScheduleDisplay => Schedule switch
    {
        ShiftSchedule.Weekdays => "Weekdays",
        ShiftSchedule.Weekend => "Weekend",
        ShiftSchedule.Rotating => "Rotating",
        _ => Schedule.ToString()
    };

    public string TimeWindow
    {
        get
        {
            if (StartMinutes is null || EndMinutes is null) return "—";
            var s = TimeSpan.FromMinutes(StartMinutes.Value);
            var e = TimeSpan.FromMinutes(EndMinutes.Value);
            return $"{s.Hours:D2}:{s.Minutes:D2} – {e.Hours:D2}:{e.Minutes:D2}";
        }
    }
}

public enum ShiftType { Morning, Mid, Night }
public enum ShiftSchedule { Weekdays, Weekend, Rotating }
