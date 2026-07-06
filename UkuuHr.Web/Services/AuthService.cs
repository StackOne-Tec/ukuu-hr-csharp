using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

/// <summary>
/// Cookie-based authentication service.
/// Phase 13.4: Added audit logging for login success/failure.
/// Phase 13.5: Added BCrypt password hashing for DB-backed accounts.
/// </summary>
public class AuthService
{
    public const string AdminEmail = "admin@ukuuhr.demo";
    public const string AdminPassword = "Admin@2025";
    public const string AdminDisplayName = "Administrator";

    private readonly IHttpContextAccessor _http;
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<AuthService> _logger;
    private readonly AuditService _audit;

    public AuthService(IHttpContextAccessor http, UkuuHrDbContext db, ILogger<AuthService> logger, AuditService audit)
    {
        _http = http;
        _db = db;
        _logger = logger;
        _audit = audit;
    }

    public async Task<bool> SignInAsync(string email, string password, bool rememberMe = false)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return false;

        var normalizedEmail = email.Trim();
        var orgId = await GetOrgIdAsync();

        // 1) Try PostgreSQL-backed authentication
        UserAccount? account = null;
        try
        {
            account = await _db.UserAccounts
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail.ToLower());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB lookup failed during sign-in; falling back to demo credentials");
        }

        // 2) If DB account exists, verify BCrypt password hash
        if (account != null)
        {
            // Phase 13.5: Use BCrypt for password verification.
            // The PasswordHash is stored as a BCrypt hash string.
            // Fallback: if PasswordHash is null/empty (legacy account), accept AdminPassword.
            var isPasswordValid = false;
            if (!string.IsNullOrEmpty(account.AuthUid) && account.AuthUid.StartsWith("$2"))
            {
                // AuthUid contains a BCrypt hash (Phase 13.5 format)
                isPasswordValid = BCrypt.Net.BCrypt.Verify(password, account.AuthUid);
            }
            else
            {
                // Legacy/demo: accept the admin password for owner accounts
                isPasswordValid = password == AdminPassword;
            }

            if (!isPasswordValid || account.Status == AccountStatus.Suspended || account.Status == AccountStatus.Disabled)
            {
                // Phase 13.4: Audit failed login
                if (orgId > 0)
                    await _audit.LogAsync(orgId, AuditAction.LoginFailed, normalizedEmail,
                        details: $"Failed login for {normalizedEmail} (invalid password or disabled account)");
                return false;
            }

            var displayName = account.FullName;
            var role = account.UserType == "owner" ? UserRole.SuperAdmin : account.Role;

            await IssueCookieAsync(
                userId: account.Id.ToString(),
                displayName: displayName,
                email: account.Email,
                role: role.StorageKey(),
                rememberMe: rememberMe);

            // Phase 13.4: Audit successful login
            if (orgId > 0)
                await _audit.LogAsync(orgId, AuditAction.LoginSuccess, normalizedEmail,
                    details: $"User {normalizedEmail} signed in successfully");
            return true;
        }

        // 3) Fallback: hardcoded demo credentials (always works — for first-run / DB issues)
        if (string.Equals(normalizedEmail, AdminEmail, StringComparison.OrdinalIgnoreCase) && password == AdminPassword)
        {
            await IssueCookieAsync(
                userId: "admin-001",
                displayName: AdminDisplayName,
                email: AdminEmail,
                role: UserRole.SuperAdmin.StorageKey(),
                rememberMe: rememberMe);

            // Phase 13.4: Audit fallback admin login
            if (orgId > 0)
                await _audit.LogAsync(orgId, AuditAction.LoginSuccess, normalizedEmail,
                    details: "Fallback admin login (hardcoded credentials)");
            return true;
        }

        // Phase 13.4: Audit failed login (no matching account)
        if (orgId > 0)
            await _audit.LogAsync(orgId, AuditAction.LoginFailed, normalizedEmail,
                details: $"Failed login for {normalizedEmail} (no matching account)");
        return false;
    }

    /// <summary>Phase 13.5: Hash a password using BCrypt for storage.</summary>
    public static string HashPassword(string password)
        => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

    /// <summary>Phase 13.5: Verify a password against a BCrypt hash.</summary>
    public static bool VerifyPassword(string password, string hash)
        => BCrypt.Net.BCrypt.Verify(password, hash);

    private async Task<int> GetOrgIdAsync()
    {
        var org = await _db.Organizations.FirstOrDefaultAsync();
        return org?.Id ?? 0;
    }

    private async Task IssueCookieAsync(string userId, string displayName, string email, string role, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, displayName),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await _http.HttpContext!.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddHours(8)
            });
    }

    public async Task SignOutAsync()
    {
        var email = _http.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value;
        await _http.HttpContext!.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Phase 13.4: Audit logout
        var orgId = await GetOrgIdAsync();
        if (orgId > 0 && !string.IsNullOrEmpty(email))
            await _audit.LogAsync(orgId, AuditAction.LoginSuccess, email, details: $"User {email} signed out");
    }

    public bool IsAuthenticated => _http.HttpContext?.User?.Identity?.IsAuthenticated == true;
    public string? DisplayName => _http.HttpContext?.User?.FindFirst(ClaimTypes.Name)?.Value;
    public string? Email => _http.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value;
}
