using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UkuuHr.Models;

/// <summary>
/// Core Employee entity — mirrors the Dart EmployeeSnapshot model.
/// Combines personal, employment, banking, and statutory information.
/// </summary>
public class Employee
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    // ───────────────────────── Personal ─────────────────────────
    [MaxLength(20)]
    public string? Title { get; set; }
    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? MiddleNames { get; set; }
    [Required, MaxLength(100)]
    public string Surname { get; set; } = string.Empty;
    public string FullName => !string.IsNullOrWhiteSpace($"{FirstName} {MiddleNames} {Surname}".Trim())
        ? $"{FirstName} {MiddleNames} {Surname}".Trim().Replace("  ", " ")
        : $"{FirstName} {Surname}".Trim();

    public string Initials
    {
        get
        {
            var a = string.IsNullOrWhiteSpace(FirstName) ? "" : FirstName[..1].ToUpper();
            var b = string.IsNullOrWhiteSpace(Surname) ? "" : Surname[..1].ToUpper();
            return $"{a}{b}";
        }
    }

    [MaxLength(50)]
    public string? ResidencyStatus { get; set; } // Expatriate | Local
    [MaxLength(100)]
    public string? Nationality { get; set; }
    public DateTime? DateOfBirth { get; set; }
    [MaxLength(20)]
    public string? Gender { get; set; }
    [MaxLength(100)]
    public string? NationalIdentityNumber { get; set; }
    [MaxLength(100)]
    public string? PassportNumber { get; set; }
    [MaxLength(50)]
    public string? MaritalStatus { get; set; }

    [MaxLength(40), Phone]
    public string? Phone { get; set; }
    [MaxLength(256), EmailAddress]
    public string? Email { get; set; }
    [MaxLength(300)]
    public string? StreetAddress { get; set; }
    [MaxLength(100)]
    public string? City { get; set; }
    [MaxLength(20)]
    public string? PostalCode { get; set; }
    [MaxLength(100)]
    public string? Country { get; set; }

    // Emergency contact
    [MaxLength(200)]
    public string? EmergencyContactName { get; set; }
    [MaxLength(50)]
    public string? EmergencyContactRelationship { get; set; }
    [MaxLength(256), EmailAddress]
    public string? EmergencyContactEmail { get; set; }
    [MaxLength(40), Phone]
    public string? EmergencyContactPhone { get; set; }

    // ───────────────────────── Employment ─────────────────────────
    [MaxLength(50)]
    public string? EmployeeCode { get; set; }
    [MaxLength(150)]
    public string? JobTitle { get; set; }
    [MaxLength(150)]
    public string? Department { get; set; }

    public EmploymentStatus Status { get; set; } = EmploymentStatus.Active;

    [MaxLength(50)]
    public string? EmploymentType { get; set; } // Full-time | Part-time | Contract
    [MaxLength(50)]
    public string? ContractType { get; set; } // Fixed Term | Permanent
    public DateTime? ContractEndDate { get; set; }

    public string? JobDescription { get; set; }
    [MaxLength(200)]
    public string? ReportingManagerName { get; set; }
    [MaxLength(200)]
    public string? ReportingManagerTitle { get; set; }
    [MaxLength(200)]
    public string? PlaceOfWork { get; set; }
    public int? ProbationaryPeriodMonths { get; set; }
    [MaxLength(50)]
    public string? TerminationNoticePeriod { get; set; }
    public int? HolidayEntitlementDays { get; set; }
    public bool HolidayStatutoryMinimum { get; set; }
    public double WorkHoursPerWeek { get; set; } = 40.0;
    public DateTime? JoiningDate { get; set; }
    public double? GratuityPercent { get; set; }

    // ───────────────────────── Banking & Payment ─────────────────────────
    public double BasicSalary { get; set; }
    public double? HourlyRate { get; set; }
    public string? AllowancesJson { get; set; } // serialized List<Allowance>
    [MaxLength(200)]
    public string? BankName { get; set; }
    [MaxLength(200)]
    public string? Branch { get; set; }
    [MaxLength(50)]
    public string? AccountNumber { get; set; }
    [MaxLength(40)]
    public string? MobileMoney { get; set; }
    [MaxLength(200)]
    public string? BeneficiaryName { get; set; }
    [MaxLength(100)]
    public string? RoutingNumbers { get; set; }
    [MaxLength(10)]
    public string? Currency { get; set; } = "ZMW";
    [MaxLength(50)]
    public string? IbanNumber { get; set; }
    [MaxLength(20)]
    public string? SwiftCode { get; set; }

    // ───────────────────────── Tax & Statutory ─────────────────────────
    [MaxLength(50)]
    public string? Tpin { get; set; }
    [MaxLength(50)]
    public string? NapsaNumber { get; set; }
    [MaxLength(50)]
    public string? HealthInsuranceNumber { get; set; }

    // ───────────────────────── Meta ─────────────────────────
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public ICollection<Attendance> Attendances { get; set; } = new List<Attendance>();
    public ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();
    public ICollection<PayrollRun> PayrollRuns { get; set; } = new List<PayrollRun>();
    public ICollection<EmployeeDocument> Documents { get; set; } = new List<EmployeeDocument>();

    // ───────────────────────── Computed helpers ─────────────────────────
    [JsonIgnore]
    public List<Allowance> Allowances
    {
        get => string.IsNullOrWhiteSpace(AllowancesJson)
            ? new()
            : System.Text.Json.JsonSerializer.Deserialize<List<Allowance>>(AllowancesJson) ?? new();
        set => AllowancesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    public double TotalAllowances
    {
        get
        {
            double total = 0;
            foreach (var a in Allowances)
            {
                if (a.Type == AllowanceType.Percentage)
                    total += BasicSalary * (a.Amount / 100.0);
                else
                    total += a.Amount;
            }
            return total;
        }
    }

    public double GrossSalary => BasicSalary + TotalAllowances;

    public double EffectiveHourlyRate
    {
        get
        {
            if (HourlyRate is double hr && hr > 0) return hr;
            return CalculateHourlyRate(BasicSalary);
        }
    }

    public static double CalculateHourlyRate(double basicMonthlySalary, int workDays = 26, int workHours = 8)
    {
        if (basicMonthlySalary <= 0) return 0;
        return basicMonthlySalary / workDays / workHours;
    }

    public bool IsTerminated => Status is EmploymentStatus.Inactive or EmploymentStatus.Terminated;

    public string DisplayCurrency
    {
        get
        {
            var raw = (Currency ?? "ZMW").Trim().ToUpper();
            return raw == "ZMK" ? "ZMW" : raw;
        }
    }

    public string StatusDisplay => Status switch
    {
        EmploymentStatus.Active => "Active",
        EmploymentStatus.Probation => "Probation",
        EmploymentStatus.Inactive => "Inactive",
        EmploymentStatus.Terminated => "Terminated",
        _ => Status.ToString()
    };
}

public enum EmploymentStatus
{
    Active,
    Probation,
    Inactive,
    Terminated
}

public class Allowance
{
    public string Name { get; set; } = string.Empty;
    public double Amount { get; set; }
    public AllowanceType Type { get; set; } = AllowanceType.Fixed;
    public bool Taxable { get; set; } = true;
}

public enum AllowanceType
{
    Fixed,
    Percentage
}
