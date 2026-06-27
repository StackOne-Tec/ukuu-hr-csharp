using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>
/// Represents an organization (tenant) in the multi-tenant HR system.
/// </summary>
public class Organization
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Country { get; set; } = "Zambia";

    [MaxLength(10)]
    public string Currency { get; set; } = "ZMW";

    [MaxLength(100)]
    public string? Industry { get; set; }

    /// <summary>Owner user ID.</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>JSON: country-specific payroll statutory rates (PAYE bands, NAPSA, NHIMA).</summary>
    public string PayrollConfigJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
    public ICollection<UserAccount> UserAccounts { get; set; } = new List<UserAccount>();
    public ICollection<DepartmentShiftAssignment> Shifts { get; set; } = new List<DepartmentShiftAssignment>();
    public ICollection<LeaveType> LeaveTypes { get; set; } = new List<LeaveType>();
    public ICollection<HrConversation> Conversations { get; set; } = new List<HrConversation>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
}

/// <summary>
/// Managed user account (admin or employee) inside an organization.
/// Linked to the Identity user via AuthUid.
/// </summary>
public class UserAccount
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    /// <summary>ASP.NET Core Identity user ID.</summary>
    [MaxLength(450)]
    public string AuthUid { get; set; } = string.Empty;

    [Required, MaxLength(256), EmailAddress]
    public string Email { get; set; } = string.Empty;

    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    public string FullName => string.IsNullOrWhiteSpace($"{FirstName} {LastName}".Trim()) ? Email : $"{FirstName} {LastName}".Trim();

    public string Initials
    {
        get
        {
            var a = string.IsNullOrWhiteSpace(FirstName) ? "" : FirstName[..1].ToUpper();
            var b = string.IsNullOrWhiteSpace(LastName) ? "" : LastName[..1].ToUpper();
            return $"{a}{b}";
        }
    }

    public UserRole Role { get; set; } = UserRole.Employee;

    public string UserType { get; set; } = "employee"; // owner | admin | employee

    public AccountStatus Status { get; set; } = AccountStatus.Pending;

    public bool IsFirstLogin { get; set; } = true;

    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastActivatedAt { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
}

public enum AccountStatus
{
    Pending,
    Active,
    Suspended,
    Disabled
}
