using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>
/// A company branch / location. Organizations can define multiple sites and
/// assign employees to them; attendance reports can then group by branch
/// (falling back to the employee's city when unassigned).
/// </summary>
public class Branch
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>City the branch operates in (optional — used as reporting fallback label).</summary>
    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(250)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? ContactPhone { get; set; }

    /// <summary>Soft-delete flag — deactivated branches are hidden from pickers.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string StatusDisplay => IsActive ? "Active" : "Inactive";
}
