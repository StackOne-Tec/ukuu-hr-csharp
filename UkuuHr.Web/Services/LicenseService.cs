using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Subscription & licensing (Module 1 — subscription plan management).
//
// Ties the previously disconnected pieces together:
//   • Coupon redemption now CREATES a real LicenseCode bound to the org.
//   • License activation validates + binds an issued code to the org.
//   • Employee limits are enforced (Production) per plan tier.
//
// Payment gateway integration is intentionally out of scope here — codes are
// issued by the Super Admin (or a future billing webhook) and everything else
// flows from the LicenseCode table.
// ─────────────────────────────────────────────────────────────────────────────
public class LicenseService
{
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<LicenseService> _logger;

    public LicenseService(UkuuHrDbContext db, ILogger<LicenseService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Employee head-count limits per plan tier (matches the Billing page cards).</summary>
    public static int EmployeeLimitFor(string planName) => planName switch
    {
        "Starter" => 10,
        "Professional" => 50,
        _ => int.MaxValue // Enterprise — unlimited
    };

    /// <summary>The org's currently-active license (latest valid one), or null.</summary>
    public async Task<LicenseCode?> GetActiveLicenseAsync(int orgId) =>
        await _db.LicenseCodes
            .Where(l => l.ActivatedByOrganizationId == orgId
                     && l.Status == LicenseStatus.Used
                     && l.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync();

    public sealed class LicenseStatusSummary
    {
        public LicenseCode? License { get; set; }
        public bool HasLicense { get; set; }
        public string PlanName { get; set; } = "Trial";
        public int EmployeeLimit { get; set; }
        public int EmployeeCount { get; set; }
        public int DaysRemaining { get; set; }
        public bool IsOverLimit { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>Full license posture for an org (used by /billing + enforcement checks).</summary>
    public async Task<LicenseStatusSummary> GetStatusAsync(int orgId)
    {
        var license = await GetActiveLicenseAsync(orgId);
        var employeeCount = await _db.Employees.CountAsync(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive);

        var planName = license == null ? "Trial" : PlanNameFor(license);
        var limit = license == null ? 10 : EmployeeLimitFor(planName);

        return new LicenseStatusSummary
        {
            License = license,
            HasLicense = license != null,
            PlanName = planName,
            EmployeeLimit = limit,
            EmployeeCount = employeeCount,
            DaysRemaining = license != null ? Math.Max(0, (int)(license.ExpiresAt - DateTime.UtcNow).TotalDays) : 0,
            IsOverLimit = employeeCount >= limit,
            Message = license == null
                ? "No active license — running in trial mode."
                : null
        };
    }

    private static string PlanNameFor(LicenseCode license)
    {
        // Annual licenses map to Professional; Monthly to Starter unless noted otherwise.
        return license.PlanType == LicensePlanType.Annual ? "Professional" : "Starter";
    }

    /// <summary>
    /// Activate an issued license code for the calling org. Codes are single-org:
    /// a code already bound to another org is rejected; re-activating the org's
    /// own current code is an idempotent no-op success.
    /// </summary>
    public async Task<(bool success, string message)> ActivateAsync(int orgId, string code, string activatedByEmail)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var license = await _db.LicenseCodes.FirstOrDefaultAsync(l => l.Code == normalized);
        if (license == null)
            return (false, "License code not found. Check for typos or contact support.");

        if (license.Status == LicenseStatus.Revoked)
            return (false, "This license code has been revoked.");

        if (license.ExpiresAt <= DateTime.UtcNow)
            return (false, "This license code has expired.");

        if (license.ActivatedByOrganizationId.HasValue
            && license.ActivatedByOrganizationId.Value != orgId)
            return (false, "This license code is already activated by another organization.");

        if (license.ActivatedByOrganizationId == orgId)
            return (true, $"License already active — {PlanNameFor(license)} plan expires {license.ExpiresAt:dd MMM yyyy}.");

        license.Status = LicenseStatus.Used;
        license.ActivatedAt = DateTime.UtcNow;
        license.ActivatedByOrganizationId = orgId;
        license.ActivatedByEmail = activatedByEmail;
        await _db.SaveChangesAsync();

        _logger.LogInformation("License {Code} activated by org {OrgId} ({Email})", normalized, orgId, activatedByEmail);
        return (true, $"{PlanNameFor(license)} plan activated — valid until {license.ExpiresAt:dd MMM yyyy}.");
    }

    /// <summary>
    /// Redeem a coupon: records the redemption AND provisions a real license
    /// (previously the coupon granted nothing). Monthly coupons grant 30 days,
    /// Annual coupons grant 365 days, starting now.
    /// </summary>
    public async Task<(bool success, string message, LicenseCode? license)> RedeemCouponAsync(int orgId, string couponCode, string redeemedByEmail)
    {
        var normalized = couponCode.Trim().ToUpperInvariant();
        var coupon = await _db.CouponCodes.FirstOrDefaultAsync(c => c.Code == normalized);
        if (coupon == null)
            return (false, "Coupon code not found.", null);
        if (!coupon.IsValid)
            return (false, $"Coupon is not redeemable ({coupon.StatusDisplay}).", null);

        // Duplicate-redemption guard for this org.
        var alreadyRedeemed = await _db.CouponRedemptions
            .AnyAsync(r => r.CouponCodeId == coupon.Id && r.OrganizationId == orgId);
        if (alreadyRedeemed)
            return (false, "Your organization has already redeemed this coupon.", null);

        // 1. Record the redemption.
        _db.CouponRedemptions.Add(new CouponRedemption
        {
            CouponCodeId = coupon.Id,
            OrganizationId = orgId,
            RedeemedByEmail = redeemedByEmail,
            RedeemedAt = DateTime.UtcNow
        });
        coupon.UsedCount++;

        // 2. Provision (or extend) the org's license.
        var planType = string.Equals(coupon.ApplicablePlan, "Annual", StringComparison.OrdinalIgnoreCase)
            ? LicensePlanType.Annual
            : LicensePlanType.Monthly;
        var durationDays = planType == LicensePlanType.Annual ? 365 : 30;

        var existingLicense = await GetActiveLicenseAsync(orgId);
        LicenseCode license;
        if (existingLicense != null)
        {
            // Extend from the current expiry.
            existingLicense.ExpiresAt = existingLicense.ExpiresAt.AddDays(durationDays);
            existingLicense.PlanType = planType;
            existingLicense.Notes = $"Extended {durationDays}d via coupon {coupon.Code}.";
            license = existingLicense;
        }
        else
        {
            license = new LicenseCode
            {
                Code = $"UKUU-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                PlanType = planType,
                Status = LicenseStatus.Used,
                ExpiresAt = DateTime.UtcNow.AddDays(durationDays),
                ActivatedAt = DateTime.UtcNow,
                ActivatedByOrganizationId = orgId,
                ActivatedByEmail = redeemedByEmail,
                Notes = $"Provisioned via coupon {coupon.Code} ({coupon.DiscountPercent}% discount)."
            };
            _db.LicenseCodes.Add(license);
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Coupon {Coupon} redeemed by org {OrgId} → license until {Expiry}",
            coupon.Code, orgId, license.ExpiresAt);

        return (true,
            $"Coupon redeemed — your {PlanNameFor(license)} plan is now valid until {license.ExpiresAt:dd MMM yyyy}.",
            license);
    }

    /// <summary>
    /// Enforcement hook: may the org add another employee? Returns (allowed, reason).
    /// Active in Production; Development keeps an open door so demos/tests are
    /// never locked out.
    /// </summary>
    public async Task<(bool allowed, string? reason)> CanAddEmployeeAsync(int orgId, IHostEnvironment env)
    {
        if (!env.IsProduction())
            return (true, null);

        var status = await GetStatusAsync(orgId);
        if (status.EmployeeCount >= status.EmployeeLimit)
            return (false,
                $"Employee limit reached ({status.EmployeeCount}/{status.EmployeeLimit} on the {status.PlanName} plan). " +
                "Upgrade or deactivate employees to add more.");

        if (!status.HasLicense && status.EmployeeCount >= 10)
            return (false, "Trial limit reached (10 employees). Activate a license to continue.");

        return (true, null);
    }

    /// <summary>Provision a 30-day Professional trial license for a brand-new org (signup flow).</summary>
    public async Task ProvisionTrialAsync(int orgId, string ownerEmail)
    {
        _db.LicenseCodes.Add(new LicenseCode
        {
            Code = $"UKUU-TRIAL-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            PlanType = LicensePlanType.Annual, // Annual tier = Professional features
            Status = LicenseStatus.Used,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            ActivatedAt = DateTime.UtcNow,
            ActivatedByOrganizationId = orgId,
            ActivatedByEmail = ownerEmail,
            Notes = "30-day trial provisioned at signup."
        });
        await _db.SaveChangesAsync();
    }
}
