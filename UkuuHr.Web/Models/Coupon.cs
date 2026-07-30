using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>
/// A coupon code that super admins issue to organizations for subscription management.
/// Coupons can be redeemed by org admins to activate/upgrade their subscription.
/// </summary>
public class CouponCode
{
    public int Id { get; set; }

    /// <summary>The unique coupon code string (e.g. "UKUU-SUPER-2026").</summary>
    [Required, MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this coupon provides.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Discount percentage (0–100). 100 = completely free.</summary>
    public int DiscountPercent { get; set; } = 100;

    /// <summary>Plan this coupon applies to: Monthly, Annual, or both.</summary>
    [MaxLength(20)]
    public string ApplicablePlan { get; set; } = "Annual";

    /// <summary>Maximum number of redemptions allowed (0 = unlimited).</summary>
    public int MaxUses { get; set; } = 1;

    /// <summary>How many times this coupon has been redeemed.</summary>
    public int UsedCount { get; set; }

    /// <summary>Coupon expiry date.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether this coupon is still active (can be soft-revoked).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Email of the super admin who created this coupon.</summary>
    [MaxLength(256)]
    public string CreatedByEmail { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ───── Computed helpers ─────

    public bool IsValid =>
        IsActive
        && DateTime.UtcNow < ExpiresAt
        && (MaxUses == 0 || UsedCount < MaxUses);

    public int RemainingUses => MaxUses == 0 ? -1 : Math.Max(0, MaxUses - UsedCount);

    public string StatusDisplay => !IsActive ? "Revoked"
        : DateTime.UtcNow >= ExpiresAt ? "Expired"
        : UsedCount >= MaxUses && MaxUses > 0 ? "Fully Used"
        : "Active";

    public string RedemptionLabel => MaxUses == 0
        ? $"{UsedCount} uses (unlimited)"
        : $"{UsedCount} / {MaxUses} uses";

    public string ExpiryDisplay => ExpiresAt.ToString("dd MMM yyyy");
}

/// <summary>Records a coupon redemption by an organization.</summary>
public class CouponRedemption
{
    public int Id { get; set; }

    public int CouponCodeId { get; set; }
    public CouponCode CouponCode { get; set; } = null!;

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [MaxLength(256)]
    public string RedeemedByEmail { get; set; } = string.Empty;

    public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;
}
