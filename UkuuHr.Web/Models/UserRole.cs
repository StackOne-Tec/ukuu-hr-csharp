namespace UkuuHr.Models;

/// <summary>
/// User roles for the HR system RBAC.
/// </summary>
public enum UserRole
{
    SuperAdmin,
    HrAdmin,
    FinancePayrollAdmin,
    HrOperator,
    FinancePayroll,
    Employee,
    Guest
}

/// <summary>
/// Modules of the workspace that can be gated per role.
/// </summary>
public enum WorkspaceModule
{
    Employees,
    Attendance,
    Scheduling,
    Payroll,
    Leave,
    Reports,
    Settings,
    SecurityAccess,
    PendingRegistrations
}

public static class UserRoleExtensions
{
    public static string StorageKey(this UserRole role) => role switch
    {
        UserRole.SuperAdmin => "super_admin",
        UserRole.HrAdmin => "hr_admin",
        UserRole.FinancePayrollAdmin => "finance_payroll_admin",
        UserRole.HrOperator => "hr_operator",
        UserRole.FinancePayroll => "finance_payroll",
        UserRole.Employee => "employee",
        _ => "guest"
    };

    public static string DisplayName(this UserRole role) => role switch
    {
        UserRole.SuperAdmin => "Super Admin",
        UserRole.HrAdmin => "HR Admin",
        UserRole.FinancePayrollAdmin => "Finance & Payroll Admin",
        UserRole.HrOperator => "HR Operator",
        UserRole.FinancePayroll => "Finance & Payroll",
        UserRole.Employee => "Employee",
        _ => "Guest"
    };

    public static bool IsAdmin(this UserRole role) => role is
        UserRole.SuperAdmin or
        UserRole.HrAdmin or
        UserRole.FinancePayrollAdmin or
        UserRole.HrOperator or
        UserRole.FinancePayroll;

    public static HashSet<WorkspaceModule> Modules(this UserRole role) => role switch
    {
        UserRole.SuperAdmin => new()
        {
            WorkspaceModule.Employees, WorkspaceModule.Attendance, WorkspaceModule.Scheduling,
            WorkspaceModule.Payroll, WorkspaceModule.Leave, WorkspaceModule.Reports,
            WorkspaceModule.Settings, WorkspaceModule.SecurityAccess, WorkspaceModule.PendingRegistrations
        },
        UserRole.HrAdmin => new()
        {
            WorkspaceModule.Employees, WorkspaceModule.Attendance, WorkspaceModule.Scheduling,
            WorkspaceModule.Leave, WorkspaceModule.Reports, WorkspaceModule.Settings, WorkspaceModule.PendingRegistrations
        },
        UserRole.FinancePayrollAdmin => new()
        {
            WorkspaceModule.Payroll, WorkspaceModule.Reports, WorkspaceModule.Settings
        },
        UserRole.HrOperator => new()
        {
            WorkspaceModule.Employees, WorkspaceModule.Attendance, WorkspaceModule.Leave, WorkspaceModule.Scheduling
        },
        UserRole.FinancePayroll => new()
        {
            WorkspaceModule.Payroll, WorkspaceModule.Reports
        },
        _ => new()
    };

    public static bool HasModule(this UserRole role, WorkspaceModule module) => role.Modules().Contains(module);
    public static bool CanSeeHrWorkspace(this UserRole role) => role is UserRole.SuperAdmin or UserRole.HrAdmin or UserRole.HrOperator;
    public static bool CanSeeFinanceWorkspace(this UserRole role) => role is UserRole.SuperAdmin or UserRole.FinancePayrollAdmin or UserRole.FinancePayroll;
    public static bool CanManageUserAccess(this UserRole role) => role is UserRole.SuperAdmin or UserRole.HrAdmin;
    public static bool CanManageLeaveEntitlements(this UserRole role) => role is UserRole.SuperAdmin or UserRole.HrAdmin;

    public static string BadgeColor(this UserRole role) => role switch
    {
        UserRole.SuperAdmin => "#25163F",
        UserRole.HrAdmin => "#4A3C68",
        UserRole.FinancePayrollAdmin => "#6E5F92",
        UserRole.HrOperator => "#5E4B85",
        UserRole.FinancePayroll => "#7B6A9F",
        UserRole.Employee => "#16A34A",
        _ => "#9CA3AF"
    };
}
