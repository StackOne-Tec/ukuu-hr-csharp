using UkuuHr.Models;
using UkuuHr.Services;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Unit tests for the ShiftEngine — the pure business logic that drives
/// FR-002 (duplicate detection), FR-003 (tolerance), FR-004 (shift resolution),
/// and FR-005 (multi-shift + overnight).
/// </summary>
public class ShiftEngineTests
{
    // ───────────── FR-003: Tolerance computation ─────────────

    private static AttendanceTolerance DefaultTolerance() => new()
    {
        LateCheckInToleranceMinutes = 15,
        VeryLateThresholdMinutes = 60,
        EarlyCheckOutToleranceMinutes = 10,
        HalfDayEarlyThresholdMinutes = 180,
        EarlyArrivalAllowanceMinutes = 30,
        CapEarlyArrivalToAllowance = true,
        MinPresentMinutesForAttendance = 240,
        AutoMarkAbsentWhenNoClockEvent = true,
        GracePeriodMinutes = 0,
        GracePeriodDaysMask = 0b0011111,
        DefaultBreakMinutes = 60,
        MinWorkedMinutesBeforeBreak = 240,
        HalfDayWorkedMinutes = 240
    };

    private static Shift DayShift() => new()
    {
        Id = 1,
        Name = "Day",
        Kind = ShiftKind.Fixed,
        StartMinutes = 8 * 60,
        EndMinutes = 17 * 60,
        BreakMinutes = 60,
        DaysOfWeekMask = 0b0011111
    };

    private static ShiftResolution WorkingDay(Shift shift, DateTime date)
    {
        var (start, end) = ShiftEngine.ComputeExpectedWindow(shift, date);
        return new ShiftResolution(shift, null, start, end, true, "test");
    }

    [Fact]
    public void OnTime_CheckIn_Returns_Present()
    {
        // Arrange: shift 08:00–17:00 on Monday 7 Jul 2025.
        var date = new DateTime(2025, 7, 7); // Monday
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance();

        // Employee checks in at 07:55 (5 min early — within tolerance) and out at 17:05.
        var checkIn = new DateTime(2025, 7, 7, 7, 55, 0);
        var checkOut = new DateTime(2025, 7, 7, 17, 5, 0);

        // Act
        var result = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

        // Assert
        Assert.Equal(AttendanceStatus.Present, result.Status);
        Assert.Equal(0, result.LateMinutes);
        Assert.Equal(0, result.EarlyMinutes);
        // Worked = 17:05 - 07:55 = 9h10m = 550 min. Break: 60 min (since 550 >= 240). Net = 490 min = 8.17h.
        Assert.True(result.WorkedHours > 8.0 && result.WorkedHours < 8.5);
    }

    [Fact]
    public void Late_CheckIn_Beyond_Tolerance_Returns_Late()
    {
        var date = new DateTime(2025, 7, 7);
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance();

        // 30 min late (tolerance is 15 min) → 15 min late.
        var checkIn = new DateTime(2025, 7, 7, 8, 30, 0);
        var checkOut = new DateTime(2025, 7, 7, 17, 5, 0);

        var result = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

        Assert.Equal(AttendanceStatus.Late, result.Status);
        Assert.Equal(15, result.LateMinutes);
    }

    [Fact]
    public void Very_Late_Beyond_Threshold_Still_Late_But_Notes_Escalation()
    {
        var date = new DateTime(2025, 7, 7);
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance();

        // 90 min late (threshold 60) → "very late".
        var checkIn = new DateTime(2025, 7, 7, 9, 45, 0); // 105 min late, 90 min beyond tolerance.
        var checkOut = new DateTime(2025, 7, 7, 17, 5, 0);

        var result = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

        Assert.Equal(AttendanceStatus.Late, result.Status);
        Assert.Contains("Very late", result.Notes ?? "");
    }

    [Fact]
    public void No_Clock_Event_AutoMarks_Absent_When_Policy_Set()
    {
        var date = new DateTime(2025, 7, 7);
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance();

        var result = ShiftEngine.ComputeStatus(resolution, tolerance, null, null, date);

        Assert.Equal(AttendanceStatus.Absent, result.Status);
        Assert.Contains("auto-marked", result.Notes ?? "");
    }

    [Fact]
    public void Short_Presence_Below_Min_Threshold_Returns_Absent()
    {
        var date = new DateTime(2025, 7, 7);
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance(); // MinPresent = 240 min = 4h.

        // Only 2h presence → absent.
        var checkIn = new DateTime(2025, 7, 7, 8, 0, 0);
        var checkOut = new DateTime(2025, 7, 7, 10, 0, 0);

        var result = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

        Assert.Equal(AttendanceStatus.Absent, result.Status);
    }

    [Fact]
    public void Very_Early_CheckOut_Returns_HalfDay()
    {
        var date = new DateTime(2025, 7, 7);
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance(); // HalfDayEarlyThreshold = 180 min.

        // Check out 4h early (240 min > 180 threshold).
        var checkIn = new DateTime(2025, 7, 7, 8, 0, 0);
        var checkOut = new DateTime(2025, 7, 7, 13, 0, 0); // 4h early.

        var result = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

        Assert.Equal(AttendanceStatus.HalfDay, result.Status);
    }

    [Fact]
    public void Early_Arrival_Capped_To_Allowance_When_Policy_Set()
    {
        var date = new DateTime(2025, 7, 7);
        var shift = DayShift();
        var resolution = WorkingDay(shift, date);
        var tolerance = DefaultTolerance(); // Allowance = 30 min, CapEarlyArrival = true.

        // Check in 2h early (06:00). Effective check-in should be capped to 07:30.
        var checkIn = new DateTime(2025, 7, 7, 6, 0, 0);
        var checkOut = new DateTime(2025, 7, 7, 17, 5, 0);

        var result = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

        // Worked = 17:05 - 07:30 (capped) = 9h35m = 575 min. Net = 575 - 60 = 515 min.
        Assert.Equal(AttendanceStatus.Present, result.Status);
        Assert.True(result.WorkedHours < 9.0); // Cap effective — would be 11h05m otherwise.
    }

    // ───────────── FR-005: Overnight shift ─────────────

    [Fact]
    public void Overnight_Shift_Expected_End_Is_Next_Day()
    {
        var shift = new Shift
        {
            Name = "Night",
            Kind = ShiftKind.Overnight,
            StartMinutes = 22 * 60, // 22:00
            EndMinutes = 6 * 60,    // 06:00 next day
            BreakMinutes = 60,
            DaysOfWeekMask = 0b0011111
        };

        var date = new DateTime(2025, 7, 7); // Monday shift starts.
        var (start, end) = ShiftEngine.ComputeExpectedWindow(shift, date);

        Assert.Equal(new DateTime(2025, 7, 7, 22, 0, 0), start);
        Assert.Equal(new DateTime(2025, 7, 8, 6, 0, 0), end);
        Assert.True(shift.IsOvernight);
    }

    [Fact]
    public void ResolveAttendanceDate_For_Overnight_After_Midnight_Returns_Previous_Day()
    {
        var shift = new Shift
        {
            Name = "Night",
            Kind = ShiftKind.Overnight,
            StartMinutes = 22 * 60,
            EndMinutes = 6 * 60,
            BreakMinutes = 60,
            DaysOfWeekMask = 0b0011111
        };

        // Employee clocks out at 02:00 on Tuesday — belongs to Monday's shift.
        var checkoutTime = new DateTime(2025, 7, 8, 2, 0, 0);
        var attDate = ShiftEngine.ResolveAttendanceDate(shift, checkoutTime);

        Assert.Equal(new DateTime(2025, 7, 7), attDate);
    }

    // ───────────── FR-005: Multi-shift resolution ─────────────

    [Fact]
    public void Resolve_Picks_Primary_When_Multiple_Match()
    {
        var date = new DateTime(2025, 7, 7); // Monday.
        var dayShift = DayShift();
        var nightShift = new Shift
        {
            Id = 2,
            Name = "Night",
            Kind = ShiftKind.Overnight,
            StartMinutes = 22 * 60,
            EndMinutes = 6 * 60,
            BreakMinutes = 60,
            DaysOfWeekMask = 0b0011111
        };

        var assignments = new List<EmployeeShiftAssignment>
        {
            new() { Shift = dayShift, ShiftId = 1, IsActive = true, IsPrimary = false,
                    EffectiveFrom = date.AddDays(-30) },
            new() { Shift = nightShift, ShiftId = 2, IsActive = true, IsPrimary = true,
                    EffectiveFrom = date.AddDays(-30) }
        };

        var resolution = ShiftEngine.Resolve(date, null, assignments);

        Assert.NotNull(resolution.Shift);
        Assert.Equal("Night", resolution.Shift.Name);
        Assert.True(resolution.Assignment?.IsPrimary);
    }

    [Fact]
    public void Resolve_Falls_Back_To_OrgWide_Shift_When_No_Personal_Assignment()
    {
        var date = new DateTime(2025, 7, 7); // Monday.
        var orgShift = DayShift();

        var resolution = ShiftEngine.Resolve(date, orgShift, new List<EmployeeShiftAssignment>());

        Assert.NotNull(resolution.Shift);
        Assert.True(resolution.IsWorkingDay);
    }

    [Fact]
    public void Resolve_Returns_DayOff_When_No_Shift_And_No_Fallback()
    {
        var date = new DateTime(2025, 7, 7); // Monday.

        var resolution = ShiftEngine.Resolve(date, null, new List<EmployeeShiftAssignment>());

        Assert.Null(resolution.Shift);
        Assert.False(resolution.IsWorkingDay);
    }

    [Fact]
    public void Resolve_Returns_DayOff_When_Shift_Doesnt_Apply_On_Weekend()
    {
        var date = new DateTime(2025, 7, 12); // Saturday.
        var weekdayOnlyShift = DayShift(); // DaysOfWeekMask = Mon-Fri.

        var resolution = ShiftEngine.Resolve(date, weekdayOnlyShift, new List<EmployeeShiftAssignment>());

        Assert.False(resolution.IsWorkingDay);
        Assert.Contains("Weekend", resolution.Reason);
    }

    // ───────────── FR-005: Rotation ─────────────

    [Fact]
    public void Rotation_Slot_Active_Only_On_Correct_Cycle()
    {
        var shift = new Shift
        {
            Name = "Rotating",
            Kind = ShiftKind.Rotating,
            RotationCycleDays = 7,
            RotationSlots = 2,
            StartMinutes = 8 * 60,
            EndMinutes = 17 * 60,
            DaysOfWeekMask = 0b0011111
        };

        var anchor = new DateTime(2025, 7, 7); // Monday — slot 0 starts.
        var assignment = new EmployeeShiftAssignment
        {
            Shift = shift,
            IsActive = true,
            IsPrimary = true,
            EffectiveFrom = anchor.AddDays(-30),
            RotationSlot = 0,
            RotationAnchorDate = anchor
        };

        // Day 0 (anchor) — slot 0 should be active.
        Assert.True(assignment.IsRotationSlotActiveOn(anchor));
        // Day 7 — slot (7/7) % 2 = 1, so slot 0 NOT active.
        Assert.False(assignment.IsRotationSlotActiveOn(anchor.AddDays(7)));
        // Day 14 — slot (14/7) % 2 = 0, so slot 0 active again.
        Assert.True(assignment.IsRotationSlotActiveOn(anchor.AddDays(14)));
    }

    // ───────────── FR-002: Duplicate detection ─────────────

    [Fact]
    public void Duplicate_Detected_Within_60_Seconds()
    {
        var empId = 1;
        var eventTime = new DateTime(2025, 7, 7, 8, 0, 0);
        var existing = new List<HikvisionClockEvent>
        {
            new() { EmployeeId = empId, EventType = ClockEventType.CheckIn, EventTime = eventTime.AddSeconds(30) }
        };

        var isDup = ShiftEngine.IsDuplicate(empId, ClockEventType.CheckIn, eventTime, existing);

        Assert.True(isDup);
    }

    [Fact]
    public void NonDuplicate_Not_Flagged_When_Outside_Window()
    {
        var empId = 1;
        var eventTime = new DateTime(2025, 7, 7, 8, 0, 0);
        var existing = new List<HikvisionClockEvent>
        {
            new() { EmployeeId = empId, EventType = ClockEventType.CheckIn, EventTime = eventTime.AddMinutes(5) }
        };

        var isDup = ShiftEngine.IsDuplicate(empId, ClockEventType.CheckIn, eventTime, existing);

        Assert.False(isDup);
    }

    [Fact]
    public void Different_EventType_Not_Duplicate_Even_If_Same_Time()
    {
        var empId = 1;
        var eventTime = new DateTime(2025, 7, 7, 8, 0, 0);
        var existing = new List<HikvisionClockEvent>
        {
            new() { EmployeeId = empId, EventType = ClockEventType.CheckOut, EventTime = eventTime }
        };

        var isDup = ShiftEngine.IsDuplicate(empId, ClockEventType.CheckIn, eventTime, existing);

        Assert.False(isDup);
    }

    // ───────────── FR-004: Validation ─────────────

    [Fact]
    public void ValidateShift_Rejects_Empty_Name()
    {
        var shift = new Shift { Name = "", StartMinutes = 480, EndMinutes = 1020, DaysOfWeekMask = 0b0011111 };
        var errors = ShiftEngine.ValidateShift(shift);
        Assert.Contains(errors, e => e.Contains("name is required"));
    }

    [Fact]
    public void ValidateShift_Rejects_Break_Longer_Than_Shift()
    {
        var shift = new Shift
        {
            Name = "Test",
            StartMinutes = 480,    // 08:00
            EndMinutes = 540,      // 09:00 — 1h shift
            BreakMinutes = 120,    // 2h break — invalid!
            DaysOfWeekMask = 0b0011111
        };
        var errors = ShiftEngine.ValidateShift(shift);
        Assert.Contains(errors, e => e.Contains("Break cannot be longer"));
    }

    [Fact]
    public void ValidateShift_Rejects_Rotating_With_Single_Slot()
    {
        var shift = new Shift
        {
            Name = "Bad Rotating",
            Kind = ShiftKind.Rotating,
            RotationSlots = 1, // invalid — must be ≥ 2.
            RotationCycleDays = 7,
            StartMinutes = 480,
            EndMinutes = 1020,
            DaysOfWeekMask = 0b0011111
        };
        var errors = ShiftEngine.ValidateShift(shift);
        Assert.Contains(errors, e => e.Contains("at least 2 rotation slots"));
    }

    [Fact]
    public void ValidateShift_Rejects_No_Days_Selected()
    {
        var shift = new Shift
        {
            Name = "No Days",
            StartMinutes = 480,
            EndMinutes = 1020,
            DaysOfWeekMask = 0
        };
        var errors = ShiftEngine.ValidateShift(shift);
        Assert.Contains(errors, e => e.Contains("day of week"));
    }

    [Fact]
    public void ValidateShift_Accepts_Valid_Fixed_Shift()
    {
        var shift = DayShift();
        var errors = ShiftEngine.ValidateShift(shift);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateTolerance_Rejects_VeryLate_Below_LateTolerance()
    {
        var t = DefaultTolerance();
        t.VeryLateThresholdMinutes = 10; // Less than LateCheckInToleranceMinutes (15).
        var errors = ShiftEngine.ValidateTolerance(t);
        Assert.Contains(errors, e => e.Contains("Very-late threshold must be greater"));
    }

    [Fact]
    public void ValidateTolerance_Accepts_Default_Config()
    {
        var t = DefaultTolerance();
        var errors = ShiftEngine.ValidateTolerance(t);
        Assert.Empty(errors);
    }

    // ───────────── FR-003: Grace period ─────────────

    [Fact]
    public void Grace_Period_Adds_To_Late_Tolerance_On_Configured_Days()
    {
        var tolerance = DefaultTolerance();
        tolerance.LateCheckInToleranceMinutes = 15;
        tolerance.GracePeriodMinutes = 10;
        tolerance.GracePeriodDaysMask = 0b0011111; // Mon-Fri.

        // Monday — grace applies.
        var monday = new DateTime(2025, 7, 7);
        var monEffective = tolerance.EffectiveLateToleranceFor(monday.DayOfWeek);
        Assert.Equal(25, monEffective); // 15 + 10.

        // Saturday — grace does NOT apply (not in mask).
        var saturday = new DateTime(2025, 7, 12);
        var satEffective = tolerance.EffectiveLateToleranceFor(saturday.DayOfWeek);
        Assert.Equal(15, satEffective); // Just the base tolerance.
    }

    [Fact]
    public void PlannedHours_For_Overnight_Includes_Wrap()
    {
        var overnight = new Shift
        {
            Name = "Night",
            Kind = ShiftKind.Overnight,
            StartMinutes = 22 * 60, // 22:00
            EndMinutes = 6 * 60,    // 06:00 next day
            BreakMinutes = 60
        };
        // 22:00 → 06:00 = 8h. Minus 1h break = 7h worked.
        Assert.Equal(8.0, overnight.PlannedHours);
        Assert.Equal(7.0, overnight.PlannedWorkedHours);
    }

    [Fact]
    public void PlannedHours_For_Day_Shift_Is_Straightforward()
    {
        var day = DayShift(); // 08:00–17:00, 1h break.
        Assert.Equal(9.0, day.PlannedHours);
        Assert.Equal(8.0, day.PlannedWorkedHours);
    }
}
