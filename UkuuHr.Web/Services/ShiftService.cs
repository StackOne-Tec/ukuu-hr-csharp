using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// ShiftService — CRUD + assignment operations for FR-003, FR-004, FR-005.
// All write operations validate via ShiftEngine and audit via AuditService.
// ─────────────────────────────────────────────────────────────────────────────

public class ShiftService
{
    private readonly UkuuHrDbContext _db;
    private readonly AuditService _audit;
    public ShiftService(UkuuHrDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    // ───────────── FR-003: Tolerance ─────────────

    /// <summary>Get the org's tolerance config, creating a default if none exists.</summary>
    public async Task<AttendanceTolerance> GetOrCreateToleranceAsync(int orgId)
    {
        var t = await _db.Set<AttendanceTolerance>().FirstOrDefaultAsync(x => x.OrganizationId == orgId);
        if (t != null) return t;
        t = new AttendanceTolerance { OrganizationId = orgId, CreatedAt = DateTime.UtcNow };
        _db.Set<AttendanceTolerance>().Add(t);
        await _db.SaveChangesAsync();
        return t;
    }

    public async Task<AttendanceTolerance> UpdateToleranceAsync(int orgId, AttendanceTolerance updated, string? actorEmail)
    {
        var existing = await _db.Set<AttendanceTolerance>().FirstOrDefaultAsync(x => x.OrganizationId == orgId)
            ?? throw new InvalidOperationException("Tolerance config not found. Call GetOrCreateToleranceAsync first.");

        // Validate before saving.
        var errors = ShiftEngine.ValidateTolerance(updated);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors));

        // Snapshot previous values for audit.
        var prev = new
        {
            existing.LateCheckInToleranceMinutes,
            existing.VeryLateThresholdMinutes,
            existing.EarlyCheckOutToleranceMinutes,
            existing.HalfDayEarlyThresholdMinutes,
            existing.MinPresentMinutesForAttendance,
            existing.GracePeriodMinutes,
            existing.DefaultBreakMinutes
        };

        existing.LateCheckInToleranceMinutes = updated.LateCheckInToleranceMinutes;
        existing.VeryLateThresholdMinutes = updated.VeryLateThresholdMinutes;
        existing.EarlyCheckOutToleranceMinutes = updated.EarlyCheckOutToleranceMinutes;
        existing.HalfDayEarlyThresholdMinutes = updated.HalfDayEarlyThresholdMinutes;
        existing.EarlyArrivalAllowanceMinutes = updated.EarlyArrivalAllowanceMinutes;
        existing.CapEarlyArrivalToAllowance = updated.CapEarlyArrivalToAllowance;
        existing.MinPresentMinutesForAttendance = updated.MinPresentMinutesForAttendance;
        existing.AutoMarkAbsentWhenNoClockEvent = updated.AutoMarkAbsentWhenNoClockEvent;
        existing.GracePeriodMinutes = updated.GracePeriodMinutes;
        existing.GracePeriodDaysMask = updated.GracePeriodDaysMask;
        existing.DefaultBreakMinutes = updated.DefaultBreakMinutes;
        existing.MinWorkedMinutesBeforeBreak = updated.MinWorkedMinutesBeforeBreak;
        existing.HalfDayWorkedMinutes = updated.HalfDayWorkedMinutes;
        existing.UpdatedByEmail = actorEmail;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.ProfileUpdated, actorEmail,
            details: "Updated attendance tolerance",
            previousValue: System.Text.Json.JsonSerializer.Serialize(prev),
            newValue: System.Text.Json.JsonSerializer.Serialize(new
            {
                existing.LateCheckInToleranceMinutes,
                existing.VeryLateThresholdMinutes,
                existing.EarlyCheckOutToleranceMinutes,
                existing.HalfDayEarlyThresholdMinutes,
                existing.MinPresentMinutesForAttendance,
                existing.GracePeriodMinutes,
                existing.DefaultBreakMinutes
            }));

        return existing;
    }

    // ───────────── FR-004: Shift CRUD ─────────────

    public Task<List<Shift>> GetAllShiftsAsync(int orgId, bool includeInactive = false)
    {
        var q = _db.Set<Shift>().Where(s => s.OrganizationId == orgId);
        if (!includeInactive) q = q.Where(s => s.IsActive);
        return q.OrderBy(s => s.Name).ToListAsync();
    }

    public Task<Shift?> GetShiftAsync(int orgId, int id) =>
        _db.Set<Shift>().FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == id);

    public async Task<Shift> CreateShiftAsync(int orgId, Shift shift, string? actorEmail)
    {
        shift.OrganizationId = orgId;
        shift.CreatedAt = DateTime.UtcNow;
        var errors = ShiftEngine.ValidateShift(shift);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors));
        _db.Set<Shift>().Add(shift);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.BulkImport, actorEmail,
            details: $"Created shift '{shift.Name}' ({shift.Kind})",
            newValue: $"{shift.Name} ({shift.KindDisplay}) {shift.TimeWindow}");
        return shift;
    }

    public async Task<Shift> UpdateShiftAsync(int orgId, Shift updated, string? actorEmail)
    {
        var existing = await _db.Set<Shift>().FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == updated.Id)
            ?? throw new InvalidOperationException("Shift not found.");
        var errors = ShiftEngine.ValidateShift(updated);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors));

        var prevName = existing.Name;
        var prevKind = existing.Kind;

        existing.Name = updated.Name;
        existing.Description = updated.Description;
        existing.Kind = updated.Kind;
        existing.Color = updated.Color;
        existing.StartMinutes = updated.StartMinutes;
        existing.EndMinutes = updated.EndMinutes;
        existing.BreakMinutes = updated.BreakMinutes;
        existing.DaysOfWeekMask = updated.DaysOfWeekMask;
        existing.RotationCycleDays = updated.RotationCycleDays;
        existing.RotationSlots = updated.RotationSlots;
        existing.FlexibleMinHours = updated.FlexibleMinHours;
        existing.FlexibleMaxHours = updated.FlexibleMaxHours;
        existing.FlexibleCoreStartMinutes = updated.FlexibleCoreStartMinutes;
        existing.FlexibleCoreEndMinutes = updated.FlexibleCoreEndMinutes;
        existing.IsActive = updated.IsActive;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.ProfileUpdated, actorEmail,
            details: $"Updated shift '{prevName}'",
            previousValue: $"{prevName} ({prevKind})",
            newValue: $"{existing.Name} ({existing.Kind})");
        return existing;
    }

    public async Task<bool> DeleteShiftAsync(int orgId, int id, string? actorEmail)
    {
        var shift = await _db.Set<Shift>().FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == id);
        if (shift == null) return false;
        // Soft-delete: just mark inactive. Active assignments remain historical.
        shift.IsActive = false;
        shift.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.UserDeleted, actorEmail,
            details: $"Deactivated shift '{shift.Name}'",
            previousValue: shift.Name);
        return true;
    }

    // ───────────── FR-005: Multiple shift assignment ─────────────

    public Task<List<EmployeeShiftAssignment>> GetAssignmentsAsync(int orgId, int? employeeId = null)
    {
        var q = _db.Set<EmployeeShiftAssignment>()
            .Include(a => a.Shift)
            .Include(a => a.Employee)
            .Where(a => a.OrganizationId == orgId);
        if (employeeId.HasValue) q = q.Where(a => a.EmployeeId == employeeId.Value);
        return q.OrderByDescending(a => a.CreatedAt).ToListAsync();
    }

    public async Task<List<EmployeeShiftAssignment>> GetAssignmentsForEmployeeAsync(int orgId, int employeeId)
    {
        return await _db.Set<EmployeeShiftAssignment>()
            .Include(a => a.Shift)
            .Where(a => a.OrganizationId == orgId && a.EmployeeId == employeeId && a.IsActive)
            .OrderByDescending(a => a.IsPrimary)
            .ThenByDescending(a => a.EffectiveFrom)
            .ToListAsync();
    }

    public async Task<EmployeeShiftAssignment> AssignShiftAsync(int orgId, EmployeeShiftAssignment assignment, string? actorEmail)
    {
        assignment.OrganizationId = orgId;
        assignment.CreatedAt = DateTime.UtcNow;
        // Verify shift + employee belong to org.
        var shift = await _db.Set<Shift>().FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == assignment.ShiftId)
            ?? throw new InvalidOperationException("Shift not found in this organization.");
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == orgId && e.Id == assignment.EmployeeId)
            ?? throw new InvalidOperationException("Employee not found in this organization.");

        // If this is marked primary, demote any existing primary.
        if (assignment.IsPrimary)
        {
            var existingPrimary = await _db.Set<EmployeeShiftAssignment>()
                .Where(a => a.OrganizationId == orgId && a.EmployeeId == assignment.EmployeeId && a.IsPrimary && a.IsActive)
                .ToListAsync();
            foreach (var p in existingPrimary) p.IsPrimary = false;
        }

        _db.Set<EmployeeShiftAssignment>().Add(assignment);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.BulkImport, actorEmail,
            details: $"Assigned shift '{shift.Name}' to {emp.FullName}",
            newValue: $"{shift.Name} → {emp.FullName} (primary={assignment.IsPrimary})");
        return assignment;
    }

    public async Task<bool> UnassignShiftAsync(int orgId, int assignmentId, string? actorEmail)
    {
        var a = await _db.Set<EmployeeShiftAssignment>()
            .Include(x => x.Shift)
            .Include(x => x.Employee)
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == assignmentId);
        if (a == null) return false;
        a.IsActive = false;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(orgId, AuditAction.UserDeleted, actorEmail,
            details: $"Unassigned shift '{a.Shift?.Name}' from {a.Employee?.FullName}");
        return true;
    }

    // ───────────── Engine integration: resolve shift for a date ─────────────

    /// <summary>Resolve the applicable shift for an employee on a date.</summary>
    public async Task<ShiftResolution> ResolveForEmployeeAsync(int orgId, int employeeId, DateTime date)
    {
        var assignments = await _db.Set<EmployeeShiftAssignment>()
            .Include(a => a.Shift)
            .Where(a => a.OrganizationId == orgId && a.EmployeeId == employeeId && a.IsActive)
            .ToListAsync();

        // Fallback: if employee has a Department, look up DepartmentShiftAssignment.
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
        Shift? fallback = null;
        if (employee?.Department != null)
        {
            // Try to map department shift to a real Shift via StartMinutes/EndMinutes.
            // For now, we use DepartmentShiftAssignment only as a fallback time window.
            // The actual shift model takes precedence.
        }

        return ShiftEngine.Resolve(date, fallback, assignments);
    }

    /// <summary>Get the org's tolerance config (no auto-create).</summary>
    public async Task<AttendanceTolerance?> GetToleranceAsync(int orgId) =>
        await _db.Set<AttendanceTolerance>().FirstOrDefaultAsync(x => x.OrganizationId == orgId);
}
