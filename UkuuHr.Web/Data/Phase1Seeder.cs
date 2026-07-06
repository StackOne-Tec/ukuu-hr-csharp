using Microsoft.EntityFrameworkCore;
using UkuuHr.Models;

namespace UkuuHr.Data;

/// <summary>
/// Phase 1 seeder: seeds AttendanceTolerance, default Shifts, and EmployeeShiftAssignments.
/// Called by DbSeeder after the main seed completes. Idempotent — checks before inserting.
/// </summary>
public static class Phase1Seeder
{
    public static async Task SeedAsync(UkuuHrDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        if (!await db.Organizations.AnyAsync()) return; // main seeder hasn't run yet

        var org = await db.Organizations.FirstAsync();

        // ───── FR-003: Default tolerance config ─────
        if (!await db.AttendanceTolerances.AnyAsync(t => t.OrganizationId == org.Id))
        {
            db.AttendanceTolerances.Add(new AttendanceTolerance
            {
                OrganizationId = org.Id,
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
                HalfDayWorkedMinutes = 240,
                UpdatedByEmail = "system@ukuuhr.demo",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // ───── FR-004: Default shift catalogue ─────
        if (!await db.Shifts.AnyAsync(s => s.OrganizationId == org.Id))
        {
            var shifts = new List<Shift>
            {
                new()
                {
                    OrganizationId = org.Id,
                    Name = "Day Shift (08:00–17:00)",
                    Description = "Standard weekday day shift with a 1-hour lunch break.",
                    Kind = ShiftKind.Fixed,
                    Color = "#25163F",
                    StartMinutes = 8 * 60,
                    EndMinutes = 17 * 60,
                    BreakMinutes = 60,
                    DaysOfWeekMask = 0b0011111, // Mon–Fri
                    IsActive = true,
                    CreatedByUserId = "system",
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    OrganizationId = org.Id,
                    Name = "Morning Flex (07:00–15:00)",
                    Description = "Flexible start between 06:30 and 09:00, core hours 10:00–15:00.",
                    Kind = ShiftKind.Flexible,
                    Color = "#6E5F92",
                    StartMinutes = 7 * 60,
                    EndMinutes = 15 * 60,
                    BreakMinutes = 45,
                    DaysOfWeekMask = 0b0011111,
                    FlexibleMinHours = 6.0,
                    FlexibleMaxHours = 9.0,
                    FlexibleCoreStartMinutes = 10 * 60,
                    FlexibleCoreEndMinutes = 15 * 60,
                    IsActive = true,
                    CreatedByUserId = "system",
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    OrganizationId = org.Id,
                    Name = "Night Shift (22:00–06:00)",
                    Description = "Overnight shift crossing midnight. Check-out rolls into the next calendar day.",
                    Kind = ShiftKind.Overnight,
                    Color = "#5E4B85",
                    StartMinutes = 22 * 60,
                    EndMinutes = 6 * 60,
                    BreakMinutes = 60,
                    DaysOfWeekMask = 0b0011111, // Mon–Fri nights
                    IsActive = true,
                    CreatedByUserId = "system",
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    OrganizationId = org.Id,
                    Name = "Rotating Day/Night (Weekly)",
                    Description = "Weekly rotation between day (08:00–17:00) and night (22:00–06:00) shifts.",
                    Kind = ShiftKind.Rotating,
                    Color = "#9C8FB8",
                    StartMinutes = 8 * 60,
                    EndMinutes = 17 * 60,
                    BreakMinutes = 60,
                    DaysOfWeekMask = 0b0011111,
                    RotationCycleDays = 7,
                    RotationSlots = 2,
                    IsActive = true,
                    CreatedByUserId = "system",
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    OrganizationId = org.Id,
                    Name = "Weekend Half-Day (08:00–13:00)",
                    Description = "Saturday-only half-day shift for operations teams.",
                    Kind = ShiftKind.Fixed,
                    Color = "#16A34A",
                    StartMinutes = 8 * 60,
                    EndMinutes = 13 * 60,
                    BreakMinutes = 0,
                    DaysOfWeekMask = 1 << 5, // Saturday only (bit 5 in our Mon=0..Sun=6 mask)
                    IsActive = true,
                    CreatedByUserId = "system",
                    CreatedAt = DateTime.UtcNow
                }
            };
            db.Shifts.AddRange(shifts);
            await db.SaveChangesAsync();

            // ───── FR-005: Multi-shift assignments ─────
            // Assign Day Shift (primary) to all employees, plus extras to specific roles.
            var employees = await db.Employees.Where(e => e.OrganizationId == org.Id).ToListAsync();
            var dayShift = shifts[0];
            var flexShift = shifts[1];
            var nightShift = shifts[2];
            var rotatingShift = shifts[3];
            var weekendShift = shifts[4];

            var assignments = new List<EmployeeShiftAssignment>();
            foreach (var emp in employees)
            {
                // Primary: Day shift for everyone.
                assignments.Add(new EmployeeShiftAssignment
                {
                    OrganizationId = org.Id,
                    EmployeeId = emp.Id,
                    ShiftId = dayShift.Id,
                    EffectiveFrom = DateTime.UtcNow.Date.AddDays(-30),
                    IsPrimary = true,
                    IsActive = true,
                    AssignedByEmail = "system@ukuuhr.demo",
                    CreatedAt = DateTime.UtcNow.AddDays(-30)
                });
            }

            // Engineering gets flex option.
            var engineers = employees.Where(e => e.Department == "Engineering").ToList();
            foreach (var eng in engineers)
            {
                assignments.Add(new EmployeeShiftAssignment
                {
                    OrganizationId = org.Id,
                    EmployeeId = eng.Id,
                    ShiftId = flexShift.Id,
                    EffectiveFrom = DateTime.UtcNow.Date.AddDays(-15),
                    IsPrimary = false,
                    IsActive = true,
                    AssignedByEmail = "system@ukuuhr.demo",
                    CreatedAt = DateTime.UtcNow.AddDays(-15)
                });
            }

            // Operations folks get weekend half-day on top of weekday.
            var ops = employees.Where(e => e.Department == "Operations" || e.Department == "Engineering").ToList();
            foreach (var op in ops)
            {
                assignments.Add(new EmployeeShiftAssignment
                {
                    OrganizationId = org.Id,
                    EmployeeId = op.Id,
                    ShiftId = weekendShift.Id,
                    EffectiveFrom = DateTime.UtcNow.Date.AddDays(-30),
                    IsPrimary = false,
                    IsActive = true,
                    AssignedByEmail = "system@ukuuhr.demo",
                    CreatedAt = DateTime.UtcNow.AddDays(-30)
                });
            }

            // First employee gets the rotating shift as a demo (replaces day shift for them).
            if (employees.Count > 0)
            {
                var rotatingEmp = employees[0];
                // Demote their day shift and add rotating as primary.
                var dayAssignment = assignments.First(a => a.EmployeeId == rotatingEmp.Id && a.ShiftId == dayShift.Id);
                dayAssignment.IsPrimary = false;
                assignments.Add(new EmployeeShiftAssignment
                {
                    OrganizationId = org.Id,
                    EmployeeId = rotatingEmp.Id,
                    ShiftId = rotatingShift.Id,
                    EffectiveFrom = DateTime.UtcNow.Date.AddDays(-7),
                    IsPrimary = true,
                    IsActive = true,
                    RotationSlot = 0,
                    RotationAnchorDate = DateTime.UtcNow.Date.AddDays(-7),
                    AssignedByEmail = "system@ukuuhr.demo",
                    CreatedAt = DateTime.UtcNow.AddDays(-7)
                });
            }

            // Night shift for the security officer (if exists).
            var security = employees.FirstOrDefault(e => e.JobTitle?.Contains("Security") == true);
            if (security != null)
            {
                assignments.Add(new EmployeeShiftAssignment
                {
                    OrganizationId = org.Id,
                    EmployeeId = security.Id,
                    ShiftId = nightShift.Id,
                    EffectiveFrom = DateTime.UtcNow.Date.AddDays(-30),
                    IsPrimary = true,
                    IsActive = true,
                    AssignedByEmail = "system@ukuuhr.demo",
                    CreatedAt = DateTime.UtcNow.AddDays(-30)
                });
            }

            db.EmployeeShiftAssignments.AddRange(assignments);
            await db.SaveChangesAsync();
        }
    }
}
