using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

// ─────────────────────────────────────────────────────────────────────────────
// FR-003 Attendance Tolerance
// Organization-level policy that defines the lateness / early-leave / absence
// thresholds used by the attendance engine to derive each Attendance.Status.
// One row per organization. Updates are audit-logged.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Organization-level attendance tolerance configuration (FR-003).
/// All values are expressed in <b>minutes</b> unless otherwise stated.
/// </summary>
public class AttendanceTolerance
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    // ───── Late check-in tolerance ─────
    /// <summary>Minutes after shift start that are still considered "On Time". Beyond this → Late.</summary>
    public int LateCheckInToleranceMinutes { get; set; } = 15;

    /// <summary>Minutes after which an employee is flagged "Very Late" (still Present, but escalated).</summary>
    public int VeryLateThresholdMinutes { get; set; } = 60;

    // ───── Early check-out tolerance ─────
    /// <summary>Minutes before shift end that are still considered "On Time" for check-out.</summary>
    public int EarlyCheckOutToleranceMinutes { get; set; } = 10;

    /// <summary>Minutes before shift end that flag a "Half Day" status.</summary>
    public int HalfDayEarlyThresholdMinutes { get; set; } = 180;

    // ───── Early arrival allowance ─────
    /// <summary>Minutes before shift start that count as paid work time (early-arrival allowance).</summary>
    public int EarlyArrivalAllowanceMinutes { get; set; } = 30;

    /// <summary>If true, early arrivals beyond the allowance are unpaid (excluded from worked hours).</summary>
    public bool CapEarlyArrivalToAllowance { get; set; } = true;

    // ───── Absent threshold ─────
    /// <summary>Minutes an employee must be present to avoid being marked Absent. Defaults to half the shift.</summary>
    public int MinPresentMinutesForAttendance { get; set; } = 240;

    /// <summary>If true, employees with no clock event at all on a working day are auto-marked Absent.</summary>
    public bool AutoMarkAbsentWhenNoClockEvent { get; set; } = true;

    // ───── Grace period ─────
    /// <summary>Grace period (minutes) applied on top of tolerance for special days (payday, weather, etc.).</summary>
    public int GracePeriodMinutes { get; set; } = 0;

    /// <summary>If set, grace only applies on these weekdays (bitmask: bit 0 = Mon ... bit 6 = Sun).</summary>
    public int GracePeriodDaysMask { get; set; } = 0b0011111; // Mon–Fri by default

    // ───── Break handling ─────
    /// <summary>Default unpaid break duration (minutes) deducted from worked hours when no break is clocked.</summary>
    public int DefaultBreakMinutes { get; set; } = 60;

    /// <summary>Minimum worked minutes before a break is deducted. Prevents short-day over-deduction.</summary>
    public int MinWorkedMinutesBeforeBreak { get; set; } = 240;

    // ───── Half-day rules ─────
    /// <summary>Minutes of work that count as a half day (used for partial-day leave alignment).</summary>
    public int HalfDayWorkedMinutes { get; set; } = 240;

    // ───── Meta ─────
    [MaxLength(450)]
    public string? UpdatedByUserId { get; set; }
    [MaxLength(256)]
    public string? UpdatedByEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ───── Helpers ─────

    /// <summary>Effective late tolerance for a given weekday, including any grace period.</summary>
    public int EffectiveLateToleranceFor(DayOfWeek day)
    {
        var bit = 1 << (int)day;
        // DayOfWeek.Sunday == 0 in .NET; our mask uses bit 0 = Monday.
        // Convert: Monday=0, ..., Sunday=6 in our mask. .NET DayOfWeek: Sunday=0..Saturday=6.
        var maskIndex = ((int)day + 6) % 7; // Sun(0)→6, Mon(1)→0, Tue(2)→1, ...
        bit = 1 << maskIndex;
        var grace = (GracePeriodDaysMask & bit) != 0 ? GracePeriodMinutes : 0;
        return LateCheckInToleranceMinutes + grace;
    }

    public int EffectiveEarlyCheckOutToleranceFor(DayOfWeek day)
    {
        var maskIndex = ((int)day + 6) % 7;
        var bit = 1 << maskIndex;
        var grace = (GracePeriodDaysMask & bit) != 0 ? GracePeriodMinutes : 0;
        return EarlyCheckOutToleranceMinutes + grace;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FR-004 Shift Management
// A shift definition. Supports Fixed, Rotating, Flexible, and Overnight shift
// types per the FRS. One shift can be assigned to many employees (M:N via
// EmployeeShiftAssignment). Overnight shifts are flagged and the engine knows
// to roll check-out into the next calendar day.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A reusable shift definition (FR-004). Shifts can be Fixed, Rotating, Flexible,
/// or Overnight. Each shift carries its own start/end times, break, grace, and
/// applicable days-of-week mask. Multiple shifts can be assigned to the same
/// employee via <see cref="EmployeeShiftAssignment"/> (FR-005).
/// </summary>
public class Shift
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Shift classification per FRS FR-004.</summary>
    public ShiftKind Kind { get; set; } = ShiftKind.Fixed;

    /// <summary>Hex color used in UI chips and the weekly coverage matrix.</summary>
    [MaxLength(20)]
    public string Color { get; set; } = "#25163F";

    // ───── Time window ─────
    /// <summary>Minutes since midnight (local) when the shift starts. For overnight shifts, this is the start of day N.</summary>
    public int StartMinutes { get; set; } = 8 * 60; // 08:00 default

    /// <summary>Minutes since midnight (local) when the shift ends. For overnight shifts, this may be less than StartMinutes (meaning next-day).</summary>
    public int EndMinutes { get; set; } = 17 * 60; // 17:00 default

    /// <summary>True when EndMinutes &lt;= StartMinutes (shift crosses midnight).</summary>
    public bool IsOvernight => EndMinutes <= StartMinutes;

    // ───── Break ─────
    /// <summary>Unpaid break duration in minutes.</summary>
    public int BreakMinutes { get; set; } = 60;

    // ───── Schedule pattern ─────
    /// <summary>Bitmask: bit 0 = Mon ... bit 6 = Sun. Days on which this shift is active by default.</summary>
    public int DaysOfWeekMask { get; set; } = 0b0011111; // Mon–Fri

    /// <summary>For Rotating shifts: rotation cycle in days (e.g. 7 = weekly, 14 = fortnightly).</summary>
    public int RotationCycleDays { get; set; } = 7;

    /// <summary>For Rotating shifts: number of distinct rotation slots (e.g. 2 = day/night, 3 = morning/evening/night).</summary>
    public int RotationSlots { get; set; } = 2;

    // ───── Flexible shift rules ─────
    /// <summary>For Flexible shifts: minimum worked hours per day to count as a full day.</summary>
    public double FlexibleMinHours { get; set; } = 6.0;

    /// <summary>For Flexible shifts: maximum worked hours per day (beyond this, overtime kicks in).</summary>
    public double FlexibleMaxHours { get; set; } = 9.0;

    /// <summary>For Flexible shifts: core-hours start (minutes since midnight). Employee must be present during core hours.</summary>
    public int? FlexibleCoreStartMinutes { get; set; }

    /// <summary>For Flexible shifts: core-hours end (minutes since midnight).</summary>
    public int? FlexibleCoreEndMinutes { get; set; }

    // ───── Status ─────
    public bool IsActive { get; set; } = true;

    // ───── Meta ─────
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ───── Computed helpers ─────
    public TimeSpan StartTime => TimeSpan.FromMinutes(StartMinutes);
    public TimeSpan EndTime => TimeSpan.FromMinutes(EndMinutes);

    /// <summary>Planned shift duration in hours (handles overnight wrap).</summary>
    public double PlannedHours
    {
        get
        {
            var span = EndMinutes - StartMinutes;
            if (span <= 0) span += 24 * 60; // overnight
            return span / 60.0;
        }
    }

    /// <summary>Effective worked-hours window after deducting the unpaid break.</summary>
    public double PlannedWorkedHours => Math.Max(0, PlannedHours - BreakMinutes / 60.0);

    public string TimeWindow
    {
        get
        {
            var s = TimeSpan.FromMinutes(StartMinutes);
            var e = TimeSpan.FromMinutes(EndMinutes);
            return $"{s.Hours:D2}:{s.Minutes:D2} – {e.Hours:D2}:{e.Minutes:D2}{(IsOvernight ? " +1" : "")}";
        }
    }

    public string KindDisplay => Kind switch
    {
        ShiftKind.Fixed => "Fixed",
        ShiftKind.Rotating => "Rotating",
        ShiftKind.Flexible => "Flexible",
        ShiftKind.Overnight => "Overnight",
        _ => Kind.ToString()
    };

    public string DaysDisplay
    {
        get
        {
            var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            var active = new List<string>();
            for (int i = 0; i < 7; i++) if ((DaysOfWeekMask & (1 << i)) != 0) active.Add(days[i]);
            return active.Count == 0 ? "—" : string.Join(", ", active);
        }
    }
}

/// <summary>Classification of shift per FRS FR-004.</summary>
public enum ShiftKind
{
    /// <summary>Same start/end every working day.</summary>
    Fixed,
    /// <summary>Cycles through a rotation pattern (e.g. day→night→off).</summary>
    Rotating,
    /// <summary>Flexitime — employee chooses start/end within core hours.</summary>
    Flexible,
    /// <summary>Crosses midnight (e.g. 22:00 → 06:00).</summary>
    Overnight
}

// ─────────────────────────────────────────────────────────────────────────────
// FR-005 Multiple Shift Assignment
// M:N join between Employee and Shift. An employee can have multiple active
// assignments (e.g. weekday Day shift + weekend Night shift). The engine
// resolves which shift applies for any given attendance date using the
// EffectiveFrom/EffectiveTo window, DaysOfWeekMask, and rotation slot.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Assignment of a <see cref="Shift"/> to an <see cref="Employee"/> (FR-005).
/// Supports date-bounded assignments and rotation slot indexing so the same
/// employee can carry multiple concurrent shifts without ambiguity.
/// </summary>
public class EmployeeShiftAssignment
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public int ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;

    // ───── Effective window ─────
    /// <summary>Date from which this assignment is active (inclusive).</summary>
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow.Date;

    /// <summary>Date until which this assignment is active (inclusive). Null = open-ended.</summary>
    public DateTime? EffectiveTo { get; set; }

    // ───── Rotation ─────
    /// <summary>
    /// For Rotating shifts: the slot index (0-based) this assignment represents.
    /// E.g. slot 0 = first rotation, slot 1 = second rotation, etc.
    /// </summary>
    public int? RotationSlot { get; set; }

    /// <summary>
    /// For Rotating shifts: the rotation anchor date. Slot index is computed as
    /// ((date - Anchor).Days / RotationCycleDays) % RotationSlots.
    /// </summary>
    public DateTime? RotationAnchorDate { get; set; }

    // ───── Override flags ─────
    /// <summary>True if this is the employee's primary shift (used when multiple shifts match the same day).</summary>
    public bool IsPrimary { get; set; } = true;

    /// <summary>Optional day-of-week override (bitmask). If non-zero, overrides the shift's own mask for this employee.</summary>
    public int? DaysOfWeekMaskOverride { get; set; }

    // ───── Status ─────
    public bool IsActive { get; set; } = true;

    // ───── Meta ─────
    [MaxLength(450)]
    public string? AssignedByUserId { get; set; }
    [MaxLength(256)]
    public string? AssignedByEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ───── Helpers ─────
    public int EffectiveDaysMask => DaysOfWeekMaskOverride ?? Shift.DaysOfWeekMask;

    public bool IsEffectiveOn(DateTime date)
    {
        var d = date.Date;
        if (d < EffectiveFrom.Date) return false;
        if (EffectiveTo.HasValue && d > EffectiveTo.Value.Date) return false;
        if (!IsActive) return false;
        var maskIndex = ((int)d.DayOfWeek + 6) % 7; // Mon=0..Sun=6
        return (EffectiveDaysMask & (1 << maskIndex)) != 0;
    }

    /// <summary>True if this assignment is the active rotation slot for the given date.</summary>
    public bool IsRotationSlotActiveOn(DateTime date)
    {
        if (Shift.Kind != ShiftKind.Rotating) return true;
        if (!RotationSlot.HasValue || !RotationAnchorDate.HasValue) return true;
        var days = (date.Date - RotationAnchorDate.Value.Date).Days;
        if (days < 0) days += ((-days / Shift.RotationCycleDays) + 1) * Shift.RotationCycleDays;
        var slot = (days / Shift.RotationCycleDays) % Shift.RotationSlots;
        return slot == RotationSlot.Value;
    }
}
