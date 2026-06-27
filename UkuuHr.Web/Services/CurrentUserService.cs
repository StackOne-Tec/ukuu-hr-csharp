using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

/// <summary>
/// Provides access to the currently signed-in user, their organization, and role.
/// Demo mode: pretends any logged-in Identity user is the Super Admin of the seeded org.
/// </summary>
public class CurrentUserService
{
    private readonly UkuuHrDbContext _db;
    private readonly IHttpContextAccessor _http;

    public CurrentUserService(UkuuHrDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public string? UserId => _http.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    public string? Email => _http.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? _http.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);

    public async Task<Organization?> GetOrganizationAsync()
    {
        // For demo: return the seeded organization
        return await _db.Organizations.FirstOrDefaultAsync();
    }

    public async Task<UserAccount?> GetUserAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(Email)) return null;
        return await _db.UserAccounts.FirstOrDefaultAsync(u => u.Email == Email);
    }

    public async Task<UserRole> GetEffectiveRoleAsync()
    {
        var acc = await GetUserAccountAsync();
        if (acc != null) return acc.Role;
        // Demo: if email is the owner email, return SuperAdmin
        var org = await GetOrganizationAsync();
        if (org != null && !string.IsNullOrWhiteSpace(Email))
            return UserRole.SuperAdmin; // demo: any signed-in user without account → super admin
        return UserRole.Guest;
    }
}
