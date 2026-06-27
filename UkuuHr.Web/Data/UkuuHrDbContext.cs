using Microsoft.EntityFrameworkCore;
using UkuuHr.Models;

namespace UkuuHr.Data;

/// <summary>
/// Entity Framework Core DbContext for the Ukuu HR system.
/// </summary>
public class UkuuHrDbContext : DbContext
{
    public UkuuHrDbContext(DbContextOptions<UkuuHrDbContext> options) : base(options) { }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveHoliday> LeaveHolidays => Set<LeaveHoliday>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<DepartmentShiftAssignment> DepartmentShifts => Set<DepartmentShiftAssignment>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<HrPolicy> HrPolicies => Set<HrPolicy>();
    public DbSet<HrConversation> HrConversations => Set<HrConversation>();
    public DbSet<HrMessage> HrMessages => Set<HrMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PendingRegistration> PendingRegistrations => Set<PendingRegistration>();
    public DbSet<LicenseCode> LicenseCodes => Set<LicenseCode>();
    public DbSet<ExpenseRequest> ExpenseRequests => Set<ExpenseRequest>();
    public DbSet<HikvisionDevice> HikvisionDevices => Set<HikvisionDevice>();
    public DbSet<HikvisionClockEvent> HikvisionClockEvents => Set<HikvisionClockEvent>();
    public DbSet<OvertimeRecord> OvertimeRecords => Set<OvertimeRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Organization
        b.Entity<Organization>(e =>
        {
            e.HasIndex(o => o.Name);
            e.HasIndex(o => o.OwnerUserId);
            e.Property(o => o.PayrollConfigJson).HasDefaultValue("{}");
        });

        // UserAccount
        b.Entity<UserAccount>(e =>
        {
            e.HasIndex(u => u.Email);
            e.HasIndex(u => u.AuthUid);
            e.HasIndex(u => new { u.OrganizationId, u.Email }).IsUnique();
            e.Property(u => u.Role).HasConversion<string>();
            e.Property(u => u.Status).HasConversion<string>();
            e.Property(u => u.UserType).HasDefaultValue("employee");
        });

        // Employee
        b.Entity<Employee>(e =>
        {
            e.HasIndex(emp => new { emp.OrganizationId, emp.EmployeeCode });
            e.HasIndex(emp => emp.Email);
            e.Property(emp => emp.Status).HasConversion<string>();
            e.Property(emp => emp.AllowancesJson).HasDefaultValue("[]");
            e.Ignore(emp => emp.Allowances);
            e.Ignore(emp => emp.FullName);
            e.Ignore(emp => emp.Initials);
            e.Ignore(emp => emp.TotalAllowances);
            e.Ignore(emp => emp.GrossSalary);
            e.Ignore(emp => emp.EffectiveHourlyRate);
            e.Ignore(emp => emp.IsTerminated);
            e.Ignore(emp => emp.DisplayCurrency);
            e.Ignore(emp => emp.StatusDisplay);
        });

        // Attendance
        b.Entity<Attendance>(e =>
        {
            e.HasIndex(a => new { a.OrganizationId, a.DateKey });
            e.HasIndex(a => new { a.EmployeeId, a.DateKey }).IsUnique();
            e.Property(a => a.Status).HasConversion<string>();
            e.Property(a => a.Source).HasConversion<string>();
        });

        // Leave
        b.Entity<LeaveRequest>(e =>
        {
            e.HasIndex(l => new { l.OrganizationId, l.Status });
            e.HasIndex(l => l.EmployeeId);
            e.Property(l => l.Status).HasConversion<string>();
            e.Ignore(l => l.RequestedDays);
            e.Ignore(l => l.PeriodLabel);
        });

        b.Entity<LeaveType>(e =>
        {
            e.HasIndex(lt => new { lt.OrganizationId, lt.Name }).IsUnique();
        });

        b.Entity<LeaveHoliday>(e =>
        {
            e.HasIndex(h => new { h.OrganizationId, h.Date });
        });

        // Payroll
        b.Entity<PayrollRun>(e =>
        {
            e.HasIndex(p => new { p.OrganizationId, p.Month, p.Year });
            e.HasIndex(p => p.EmployeeId);
            e.HasIndex(p => p.BatchId);
            e.HasIndex(p => p.ApprovalStatus);
            e.Property(p => p.Status).HasConversion<string>();
            e.Property(p => p.ApprovalStatus).HasConversion<string>();
            e.Property(p => p.PayslipDelivery).HasConversion<string>();
            e.Ignore(p => p.Gross);
            e.Ignore(p => p.TotalDeductions);
            e.Ignore(p => p.Net);
            e.Ignore(p => p.PeriodLabel);
        });

        // Scheduling
        b.Entity<DepartmentShiftAssignment>(e =>
        {
            e.HasIndex(s => new { s.OrganizationId, s.Department });
            e.Property(s => s.Shift).HasConversion<string>();
            e.Property(s => s.Schedule).HasConversion<string>();
            e.Ignore(s => s.ShiftDisplay);
            e.Ignore(s => s.ScheduleDisplay);
            e.Ignore(s => s.TimeWindow);
        });

        // Documents
        b.Entity<EmployeeDocument>(e =>
        {
            e.HasIndex(d => new { d.OrganizationId, d.EmployeeId });
            e.Property(d => d.Type).HasConversion<string>();
            e.Property(d => d.Category).HasConversion<string>();
            e.Property(d => d.Folder).HasConversion<string>();
            e.Ignore(d => d.FormattedSize);
        });

        b.Entity<HrPolicy>(e => e.HasIndex(p => p.OrganizationId));

        b.Entity<HrConversation>(e =>
        {
            e.HasIndex(c => c.OrganizationId);
            e.Property(c => c.ParticipantIds).HasDefaultValue("");
        });

        b.Entity<HrMessage>(e =>
        {
            e.HasIndex(m => m.ConversationId);
            e.Property(m => m.SenderRole).HasConversion<string>();
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => new { a.OrganizationId, a.Timestamp });
            e.Property(a => a.Action).HasConversion<string>();
            e.Ignore(a => a.ActionDisplay);
            e.Ignore(a => a.FormattedTimestamp);
        });

        b.Entity<PendingRegistration>(e =>
        {
            e.HasIndex(p => p.Email);
            e.HasIndex(p => p.Status);
            e.Property(p => p.Status).HasConversion<string>();
            e.Property(p => p.RequestedRole).HasConversion<string>();
            e.Ignore(p => p.FullName);
        });

        b.Entity<LicenseCode>(e =>
        {
            e.HasIndex(l => l.Code).IsUnique();
            e.HasIndex(l => l.Status);
            e.Property(l => l.PlanType).HasConversion<string>();
            e.Property(l => l.Status).HasConversion<string>();
            e.Ignore(l => l.IsValid);
            e.Ignore(l => l.PlanDurationDays);
            e.Ignore(l => l.PlanDisplayName);
        });

        b.Entity<ExpenseRequest>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.Property(x => x.Status).HasConversion<string>();
        });

        // Hikvision devices
        b.Entity<HikvisionDevice>(e =>
        {
            e.HasIndex(d => new { d.OrganizationId, d.IpAddress });
            e.Property(d => d.IsActive).HasDefaultValue(true);
        });

        // Hikvision clock events
        b.Entity<HikvisionClockEvent>(e =>
        {
            e.HasIndex(c => new { c.OrganizationId, c.EventTime });
            e.HasIndex(c => new { c.DeviceId, c.EventTime });
            e.HasIndex(c => c.IsProcessed);
            e.Property(c => c.EventType).HasConversion<string>();
            e.Ignore(c => c.EventTimeDisplay);
            e.Ignore(c => c.EventTypeDisplay);
        });

        // Overtime records
        b.Entity<OvertimeRecord>(e =>
        {
            e.HasIndex(o => new { o.OrganizationId, o.Date });
            e.HasIndex(o => new { o.OrganizationId, o.Status });
            e.HasIndex(o => o.EmployeeId);
            e.Property(o => o.RateType).HasConversion<string>();
            e.Property(o => o.Source).HasConversion<string>();
            e.Property(o => o.Status).HasConversion<string>();
            e.Ignore(o => o.Pay);
            e.Ignore(o => o.RateTypeDisplay);
            e.Ignore(o => o.StatusDisplay);
            e.Ignore(o => o.DateDisplay);
            e.Ignore(o => o.TimeWindow);
        });

        // Leave balances
        b.Entity<LeaveBalance>(e =>
        {
            e.HasIndex(lb => new { lb.OrganizationId, lb.EmployeeId, lb.Year });
            e.HasIndex(lb => new { lb.EmployeeId, lb.LeaveTypeId, lb.Year }).IsUnique();
            e.Ignore(lb => lb.RemainingDays);
        });
    }
}
