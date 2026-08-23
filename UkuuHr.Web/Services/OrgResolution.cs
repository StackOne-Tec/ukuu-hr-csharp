using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Multi-tenant organization resolution (P0 security fix)
//
// Before this existed, ~120 call sites resolved the "current" organization with
// Organizations.FirstOrDefaultAsync() — i.e. always the FIRST org in the table —
// and API endpoints trusted a client-supplied ?orgId= parameter. With more than
// one organization registered, any user could read (and write) another tenant's
// data.
//
// Resolution order (first hit wins):
//   1. ResolvedOrgId on HttpContext.Items — set by the /api/* middleware for
//      DB-API-key authentication (the key belongs to exactly one org).
//   2. The authenticated cookie user's UserAccount → its OrganizationId.
//   3. An explicit orgId parameter — ONLY when the caller is unauthenticated
//      (backward compatibility for CLI / LAN-bridge style calls; an
//      authenticated principal can never be steered into another org).
//   4. The first organization (single-tenant deployments, dev/demo mode).
// ─────────────────────────────────────────────────────────────────────────────
public static class OrgResolution
{
    private static IHttpContextAccessor? _accessor;

    /// <summary>Wire the ambient HttpContext accessor (called once at startup).</summary>
    public static void Configure(IHttpContextAccessor accessor) => _accessor = accessor;

    private static HttpContext? HttpContext => _accessor?.HttpContext;

    /// <summary>Resolve the effective organization id for the current request. 0 = no org.</summary>
    public static async Task<int> ResolveOrgIdAsync(this UkuuHrDbContext db, int? orgIdParam = null)
    {
        var ctx = HttpContext;

        // 1. API-key org (resolved by the /api/* middleware).
        if (ctx?.Items.TryGetValue("ResolvedOrgId", out var resolved) == true
            && resolved is int keyOrgId && keyOrgId > 0)
            return keyOrgId;

        // 2. Cookie-authenticated user → their account's org.
        var email = ctx?.User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var accountOrgId = await db.UserAccounts
                .Where(u => u.Email == email)
                .Select(u => (int?)u.OrganizationId)
                .FirstOrDefaultAsync();
            if (accountOrgId is > 0) return accountOrgId.Value;
        }

        // 3. Explicit orgId — trusted only for unauthenticated callers.
        if (ctx?.User?.Identity?.IsAuthenticated != true && orgIdParam is > 0)
        {
            var exists = await db.Organizations.AnyAsync(o => o.Id == orgIdParam.Value);
            if (exists) return orgIdParam.Value;
        }

        // 4. Single-org fallback (dev/demo).
        var firstOrgId = await db.Organizations
            .OrderBy(o => o.Id)
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync();
        return firstOrgId ?? 0;
    }

    /// <summary>Resolve the effective Organization entity for the current request (null when none).</summary>
    public static async Task<Organization?> ResolveOrgAsync(this UkuuHrDbContext db, int? orgIdParam = null)
    {
        var orgId = await db.ResolveOrgIdAsync(orgIdParam);
        if (orgId <= 0) return null;
        return await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId);
    }
}
