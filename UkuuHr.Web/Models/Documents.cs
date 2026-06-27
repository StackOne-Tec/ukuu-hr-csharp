using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>Document attached to an employee (contract, payslip, ID, etc.).</summary>
public class EmployeeDocument
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public DocumentType Type { get; set; } = DocumentType.Pdf;
    public DocumentCategory Category { get; set; } = DocumentCategory.Contract;
    public DocumentFolder Folder { get; set; } = DocumentFolder.All;

    public long SizeBytes { get; set; }

    [MaxLength(500)]
    public string? DownloadUrl { get; set; }

    /// <summary>HR | Employee | System</summary>
    [MaxLength(50)]
    public string? UploadedBy { get; set; }
    [MaxLength(200)]
    public string? UploadedByName { get; set; }

    public string? Description { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string FormattedSize
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            return $"{SizeBytes / (1024.0 * 1024):F1} MB";
        }
    }
}

public enum DocumentType { Pdf, Image, Word, Excel, Other }
public enum DocumentCategory { Contract, Payslip, Identity, Compliance, Policy, Other }
public enum DocumentFolder { All, MyUploads, HrShared, Compliance, Archived }

/// <summary>HR-wide policy document (not employee-specific).</summary>
public class HrPolicy
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    [MaxLength(500)]
    public string? DownloadUrl { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>In-app HR conversation between users in the same organization.</summary>
public class HrConversation
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>Comma-separated user IDs.</summary>
    public string ParticipantIds { get; set; } = string.Empty;

    public string LastMessage { get; set; } = string.Empty;
    public DateTime? LastMessageTime { get; set; }
    [MaxLength(450)]
    public string? LastSenderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<HrMessage> Messages { get; set; } = new List<HrMessage>();
}

public class HrMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public HrConversation Conversation { get; set; } = null!;

    [MaxLength(450)]
    public string SenderId { get; set; } = string.Empty;
    [MaxLength(200)]
    public string SenderName { get; set; } = string.Empty;
    public UserRole SenderRole { get; set; } = UserRole.Employee;
    [MaxLength(10)]
    public string SenderInitials { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}

/// <summary>Audit log entry for security & compliance.</summary>
public class AuditLog
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public AuditAction Action { get; set; }
    [MaxLength(450)]
    public string? TargetUserId { get; set; }
    [MaxLength(256)]
    public string? TargetUserEmail { get; set; }
    [MaxLength(450)]
    public string? PerformedByUserId { get; set; }
    [MaxLength(256)]
    public string? PerformedByEmail { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? Details { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }

    public string ActionDisplay => Action switch
    {
        AuditAction.UserCreated => "User Created",
        AuditAction.UserDeleted => "User Deleted",
        AuditAction.StatusChanged => "Status Changed",
        AuditAction.RoleChanged => "Role Changed",
        AuditAction.PasswordReset => "Password Reset",
        AuditAction.ProfileUpdated => "Profile Updated",
        AuditAction.LoginSuccess => "Login Success",
        AuditAction.LoginFailed => "Login Failed",
        AuditAction.BulkImport => "Bulk Import",
        AuditAction.BulkExport => "Bulk Export",
        _ => "Unknown"
    };

    public string FormattedTimestamp
    {
        get
        {
            var delta = DateTime.UtcNow - Timestamp;
            if (delta.TotalMinutes < 1) return "Just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
            if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
            return Timestamp.ToString("dd/MM/yyyy");
        }
    }
}

public enum AuditAction
{
    UserCreated,
    UserDeleted,
    StatusChanged,
    RoleChanged,
    PasswordReset,
    ProfileUpdated,
    LoginSuccess,
    LoginFailed,
    BulkImport,
    BulkExport,
    Unknown
}

/// <summary>Sign-up request awaiting admin approval.</summary>
public class PendingRegistration
{
    public int Id { get; set; }
    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;
    [Required, MaxLength(256), EmailAddress]
    public string Email { get; set; } = string.Empty;
    [MaxLength(40), Phone]
    public string? Phone { get; set; }
    [Required, MaxLength(200)]
    public string OrganizationName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? Country { get; set; } = "Zambia";
    [MaxLength(10)]
    public string? Currency { get; set; } = "ZMW";
    [MaxLength(100)]
    public string? Industry { get; set; }
    public UserRole RequestedRole { get; set; } = UserRole.HrAdmin;

    public PendingRegistrationStatus Status { get; set; } = PendingRegistrationStatus.Pending;
    [MaxLength(450)]
    public string? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public enum PendingRegistrationStatus { Pending, Approved, Rejected }

/// <summary>License code for activating an organization subscription.</summary>
public class LicenseCode
{
    public int Id { get; set; }

    [Required, MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    public LicensePlanType PlanType { get; set; } = LicensePlanType.Monthly;
    public LicenseStatus Status { get; set; } = LicenseStatus.Active;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public int? ActivatedByOrganizationId { get; set; }
    [MaxLength(256)]
    public string? ActivatedByEmail { get; set; }
    public string? Notes { get; set; }

    public bool IsValid => Status == LicenseStatus.Active && DateTime.UtcNow < ExpiresAt;
    public int PlanDurationDays => PlanType == LicensePlanType.Monthly ? 30 : 365;
    public string PlanDisplayName => PlanType == LicensePlanType.Monthly ? "Monthly" : "Annual";
}

public enum LicensePlanType { Monthly, Annual }
public enum LicenseStatus { Active, Used, Expired, Revoked }

/// <summary>Expense reimbursement request.</summary>
public class ExpenseRequest
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string EmployeeName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;
    [MaxLength(100)]
    public string PaymentMethod { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? Merchant { get; set; }
    public string? Description { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "ZMW";
    public double Amount { get; set; }

    public ExpenseRequestStatus Status { get; set; } = ExpenseRequestStatus.Pending;
    public DateTime ExpenseDate { get; set; }
    [MaxLength(500)]
    public string? ReceiptUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ExpenseRequestStatus { Pending, Approved, Rejected, Paid }
