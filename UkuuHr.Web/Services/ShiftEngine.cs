using UkuuHr.Models;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// ShiftEngine — pure business logic for FR-002 / FR-003 / FR-004 / FR-005.
//
// Responsibilities:
//   1. Resolve which Shift applies to an Employee on a given date (FR-005).
//      - Multi-shift support: returns the highest-priority active assignment.
//      - Rotation support: honours RotationSlot + AnchorDate.
//   2. Compute AttendanceStatus from a check-in / check-out pair (FR-003).
//      - Late tolerance, very-late threshold, early check-out tolerance.
//      - Half-day detection, absent threshold, grace period.
//   3. Compute WorkedHours correctly for overnight shifts (FR-005).
//   4. Detect duplicate clock events (FR-002).
//
// This engine is pure: no DbContext, no side effects. Inputs are POCOs.
// That keeps it trivially unit-testable. The AttendanceService wraps it and
// persists results.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Result of resolving the shift for an employee on a date.</summary>
public sealed record ShiftResolution(
    Shift? Shift,
    EmployeeShiftAssignment? Assignment,
    DateTime ExpectedStart,
    DateTime ExpectedEnd,
    bool IsWorkingDay,
    string Reason)
{
    public static ShiftResolution NoShift(string reason) =>
        new(null, null, DateTime.MinValue, DateTime.MinValue, false, reason);

    public static ShiftResolution DayOff(string reason) =>
        new(null, null, DateTime.MinValue, DateTime.MinValue, false, reason);
}

/// <summary>Result of computing attendance status from clock events.</summary>
public sealed record AttendanceComputation(
    AttendanceStatus Status,
    double WorkedHours,
    int LateMinutes,
    int EarlyMinutes,
    int BreakMinutes,
    string? Notes)
{
    public static AttendanceComputation Absent(string reason) =>
        new(AttendanceStatus.Absent, 0, 0, 0, 0, reason);
}

/// <summary>Engine for shift resolution and attendance status computation.</summary>
public static class ShiftEngine
{
    // ───────────── FR-005: Multi-shift resolution ─────────────

    /// <summary>
    /// Resolve the applicable shift for an employee on a given date.
    /// Returns the primary active assignment whose EffectiveFrom/To window,
    /// DaysOfWeekMask, and rotation slot all match.
    /// If the date is a public holiday (FR-008), returns a DayOff resolution
    /// so the attendance engine does not mark the employee absent.
    /// </summary>
    public static ShiftResolution Resolve(
        DateTime date,
        Shift? fallbackShift,
        IEnumerable<EmployeeShiftAssignment> assignments,
        HashSet<DateTime>? holidayDates = null)
    {
        var d = date.Date;

        // FR-008: If the date is a public holiday, treat it as a scheduled day off
        if (holidayDates != null && holidayDates.Contains(d))
        {
            return ShiftResolution.DayOff($"Public holiday — no attendance expected");
        }

        var candidates = assignments
            .Where(a => a.IsActive && a.IsEffectiveOn(d))
            .Where(a => a.IsRotationSlotActiveOn(d))
            .ToList();

        if (candidates.Count == 0)
        {
            // No personal assignment — fall back to org-wide shift if its mask matches.
            if (fallbackShift is { IsActive: true } s && ShiftAppliesOn(s, d))
            {
                var (es, ee) = ComputeExpectedWindow(s, d);
                return new ShiftResolution(s, null, es, ee, true, "Org-wide default shift");
            }
            // No shift at all → check if it's a weekend.
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            return ShiftResolution.DayOff(isWeekend ? "Weekend (no shift)" : "No shift assigned");
        }

        // Prefer primary if multiple match.
        var chosen = candidates.FirstOrDefault(a => a.IsPrimary) ?? candidates[0];
        var (expectedStart, expectedEnd) = ComputeExpectedWindow(chosen.Shift, d);
        return new ShiftResolution(
            chosen.Shift,
            chosen,
            expectedStart,
            expectedEnd,
            true,
            chosen.Shift.Kind == ShiftKind.Rotating
                ? $"Rotating shift — slot {chosen.RotationSlot ?? 0}"
                : chosen.Shift.Kind.ToString());
    }

    /// <summary>True if the shift's DaysOfWeekMask includes the given date.</summary>
    public static bool ShiftAppliesOn(Shift shift, DateTime date)
    {
        var maskIndex = ((int)date.DayOfWeek + 6) % 7; // Mon=0..Sun=6
        return (shift.DaysOfWeekMask & (1 << maskIndex)) != 0;
    }

    /// <summary>Compute the expected start/end DateTimes for a shift on a given date, handling overnight wrap.</summary>
    public static (DateTime start, DateTime end) ComputeExpectedWindow(Shift shift, DateTime date)
    {
        var d = date.Date;
        var start = d.Add(TimeSpan.FromMinutes(shift.StartMinutes));
        var end = shift.IsOvernight
            ? d.AddDays(1).Add(TimeSpan.FromMinutes(shift.EndMinutes))
            : d.Add(TimeSpan.FromMinutes(shift.EndMinutes));
        return (start, end);
    }

    // ───────────── FR-003: Tolerance computation ─────────────

    /// <summary>
    /// Compute the AttendanceStatus for a check-in/check-out pair against the
    /// resolved shift and the org's tolerance policy.
    /// FR-008: If the date is a public holiday, attendance is not expected.
    /// </summary>
    public static AttendanceComputation ComputeStatus(
        ShiftResolution shift,
        AttendanceTolerance tolerance,
        DateTime? checkIn,
        DateTime? checkOut,
        DateTime date)
    {
        // Case 1: No working day → no status to compute.
        // FR-008: This covers public holidays (shift.IsWorkingDay == false via Resolve).
        if (!shift.IsWorkingDay || shift.Shift is null)
        {
            // If there's a clock event on a non-working day / holiday, it's overtime-relevant
            // (handled by the overtime engine) but attendance-wise it's still a day off.
            var reason = shift.Reason.Contains("Public holiday", StringComparison.OrdinalIgnoreCase)
                ? "Public holiday — no attendance expected"
                : "Scheduled day off";
            return checkIn.HasValue
                ? new AttendanceComputation(AttendanceStatus.Remote, 0, 0, 0, 0, $"Off-day clock event ({reason})")
                : new AttendanceComputation(AttendanceStatus.Remote, 0, 0, 0, 0, reason);
        }

        var s = shift.Shift;
        var expectedStart = shift.ExpectedStart;
        var expectedEnd = shift.ExpectedEnd;

        // Case 2: No clock event at all → Absent (if org policy says so).
        if (!checkIn.HasValue && !checkOut.HasValue)
        {
            if (tolerance.AutoMarkAbsentWhenNoClockEvent)
                return AttendanceComputation.Absent("No clock event — auto-marked absent");
            return new AttendanceComputation(AttendanceStatus.Absent, 0, 0, 0, 0, "No clock event");
        }

        // Determine effective check-in (cap early arrivals if policy says so).
        var effectiveCheckIn = checkIn;
        if (checkIn.HasValue && tolerance.CapEarlyArrivalToAllowance)
        {
            var earliestAllowed = expectedStart.AddMinutes(-tolerance.EarlyArrivalAllowanceMinutes);
            if (checkIn.Value < earliestAllowed)
                effectiveCheckIn = earliestAllowed;
        }

        // Late minutes (only if check-in is after the late tolerance window).
        var lateMinutes = 0;
        if (effectiveCheckIn.HasValue)
        {
            var lateTolerance = tolerance.EffectiveLateToleranceFor(date.DayOfWeek);
            var onTimeDeadline = expectedStart.AddMinutes(lateTolerance);
            if (effectiveCheckIn.Value > onTimeDeadline)
                lateMinutes = (int)Math.Round((effectiveCheckIn.Value - onTimeDeadline).TotalMinutes);
        }

        // Early minutes (only if check-out is before shift end minus tolerance).
        var earlyMinutes = 0;
        if (checkOut.HasValue)
        {
            var earlyTolerance = tolerance.EffectiveEarlyCheckOutToleranceFor(date.DayOfWeek);
            var onTimeEarliest = expectedEnd.AddMinutes(-earlyTolerance);
            if (checkOut.Value < onTimeEarliest)
                earlyMinutes = (int)Math.Round((onTimeEarliest - checkOut.Value).TotalMinutes);
        }

        // Worked hours (handle overnight).
        var workedMinutes = ComputeWorkedMinutes(effectiveCheckIn, checkOut, s);

        // Break deduction.
        var breakMinutes = s.BreakMinutes;
        if (workedMinutes < tolerance.MinWorkedMinutesBeforeBreak)
            breakMinutes = 0; // short day — don't deduct full break

        var netWorkedMinutes = Math.Max(0, workedMinutes - breakMinutes);
        var workedHours = netWorkedMinutes / 60.0;

        // ─── Determine final status ───
        AttendanceStatus status;
        string? notes = null;

        // Absent threshold: not enough presence.
        if (netWorkedMinutes < tolerance.MinPresentMinutesForAttendance)
        {
            status = AttendanceStatus.Absent;
            notes = $"Only {netWorkedMinutes} min present (min {tolerance.MinPresentMinutesForAttendance})";
        }
        // Half-day: very early check-out or short shift.
        else if (earlyMinutes >= tolerance.HalfDayEarlyThresholdMinutes ||
                 netWorkedMinutes <= tolerance.HalfDayWorkedMinutes)
        {
            status = AttendanceStatus.HalfDay;
            notes = earlyMinutes >= tolerance.HalfDayEarlyThresholdMinutes
                ? $"Left {earlyMinutes} min early (half-day threshold {tolerance.HalfDayEarlyThresholdMinutes})"
                : $"Worked {netWorkedMinutes} min (half-day ≤ {tolerance.HalfDayWorkedMinutes})";
        }
        // Very late.
        else if (lateMinutes >= tolerance.VeryLateThresholdMinutes)
        {
            status = AttendanceStatus.Late;
            notes = $"Very late by {lateMinutes} min (threshold {tolerance.VeryLateThresholdMinutes})";
        }
        // Late.
        else if (lateMinutes > 0)
        {
            status = AttendanceStatus.Late;
            notes = $"Late by {lateMinutes} min";
        }
        // On time (full day).
        else
        {
            status = AttendanceStatus.Present;
            notes = null;
        }

        return new AttendanceComputation(status, workedHours, lateMinutes, earlyMinutes, breakMinutes, notes);
    }

    /// <summary>Compute worked minutes between check-in and check-out, handling overnight shifts.</summary>
    public static int ComputeWorkedMinutes(DateTime? checkIn, DateTime? checkOut, Shift shift)
    {
        if (!checkIn.HasValue) return 0;
        var end = checkOut ?? DateTime.UtcNow;
        var start = checkIn.Value;
        if (end < start) return 0; // defensive — shouldn't happen
        return (int)Math.Round((end - start).TotalMinutes);
    }

    // ───────────── FR-002: Duplicate detection ─────────────

    /// <summary>
    /// True if the given clock event is a duplicate of an existing one.
    /// A duplicate = same employee + same direction + within 60 seconds of an existing event.
    /// </summary>
    public static bool IsDuplicate(
        int employeeId,
        ClockEventType eventType,
        DateTime eventTime,
        IEnumerable<HikvisionClockEvent> existingEvents,
        TimeSpan? duplicateWindow = null)
    {
        var window = duplicateWindow ?? TimeSpan.FromSeconds(60);
        return existingEvents.Any(e =>
            e.EmployeeId == employeeId &&
            e.EventType == eventType &&
            Math.Abs((e.EventTime - eventTime).TotalSeconds) < window.TotalSeconds);
    }

    // ───────────── FR-005: Cross-day (overnight) attendance matching ─────────────

    /// <summary>
    /// Given an employee's clock events that span midnight, attribute the
    /// check-out to the correct attendance date (the shift's start date, not
    /// the calendar date of the check-out punch).
    /// </summary>
    public static DateTime ResolveAttendanceDate(Shift shift, DateTime eventTime)
    {
        if (!shift.IsOvernight) return eventTime.Date;
        // If event is after midnight but before shift end, it belongs to yesterday's shift.
        var eventMinutes = eventTime.TimeOfDay.TotalMinutes;
        if (eventMinutes < shift.EndMinutes)
            return eventTime.Date.AddDays(-1);
        return eventTime.Date;
    }

    // ───────────── FR-004: Shift CRUD validation ─────────────

    /// <summary>Returns a list of validation errors for the given shift definition.</summary>
    public static List<string> ValidateShift(Shift shift)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(shift.Name))
            errors.Add("Shift name is required.");
        if (shift.Name.Length > 150)
            errors.Add("Shift name must be 150 characters or fewer.");
        if (shift.StartMinutes < 0 || shift.StartMinutes >= 24 * 60)
            errors.Add("Shift start time must be between 00:00 and 23:59.");
        if (shift.EndMinutes < 0 || shift.EndMinutes >= 24 * 60)
            errors.Add("Shift end time must be between 00:00 and 23:59.");
        if (shift.BreakMinutes < 0)
            errors.Add("Break minutes cannot be negative.");
        if (shift.BreakMinutes >= (shift.PlannedHours * 60))
            errors.Add("Break cannot be longer than the shift itself.");
        if (shift.DaysOfWeekMask == 0)
            errors.Add("At least one day of week must be selected.");
        if (shift.Kind == ShiftKind.Rotating && shift.RotationSlots < 2)
            errors.Add("Rotating shifts must have at least 2 rotation slots.");
        if (shift.Kind == ShiftKind.Rotating && shift.RotationCycleDays < 1)
            errors.Add("Rotating shifts must have a rotation cycle of at least 1 day.");
        if (shift.Kind == ShiftKind.Flexible)
        {
            if (shift.FlexibleMinHours < 0 || shift.FlexibleMaxHours < shift.FlexibleMinHours)
                errors.Add("Flexible min/max hours are inconsistent.");
            if (shift.FlexibleCoreStartMinutes.HasValue && shift.FlexibleCoreEndMinutes.HasValue &&
                shift.FlexibleCoreEndMinutes.Value <= shift.FlexibleCoreStartMinutes.Value)
                errors.Add("Flexible core-hours end must be after start.");
        }
        return errors;
    }

    /// <summary>Returns a list of validation errors for the given tolerance config.</summary>
    public static List<string> ValidateTolerance(AttendanceTolerance t)
    {
        var errors = new List<string>();
        if (t.LateCheckInToleranceMinutes < 0) errors.Add("Late tolerance cannot be negative.");
        if (t.VeryLateThresholdMinutes <= t.LateCheckInToleranceMinutes)
            errors.Add("Very-late threshold must be greater than late tolerance.");
        if (t.EarlyCheckOutToleranceMinutes < 0) errors.Add("Early check-out tolerance cannot be negative.");
        if (t.HalfDayEarlyThresholdMinutes <= t.EarlyCheckOutToleranceMinutes)
            errors.Add("Half-day threshold must be greater than early check-out tolerance.");
        if (t.MinPresentMinutesForAttendance < 0) errors.Add("Absent threshold cannot be negative.");
        if (t.DefaultBreakMinutes < 0) errors.Add("Default break cannot be negative.");
        if (t.MinWorkedMinutesBeforeBreak < 0) errors.Add("Min worked before break cannot be negative.");
        if (t.GracePeriodMinutes < 0) errors.Add("Grace period cannot be negative.");
        return errors;
    }
}
