using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

/// <summary>
/// Cookie-based authentication service.
/// Verifies credentials against the PostgreSQL database (UserAccounts table).
/// Falls back to hardcoded demo credentials if no matching DB account is found
/// (useful for first-run before seeding completes).
/// </summary>
public class AuthService
{
    public const string AdminEmail = "admin@ukuuhr.demo";
    public const string AdminPassword = "Admin@2025";
    public const string AdminDisplayName = "Chungu Chama";

    private readonly IHttpContextAccessor _http;
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IHttpContextAccessor http, UkuuHrDbContext db, ILogger<AuthService> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public async Task<bool> SignInAsync(string email, string password, bool rememberMe = false)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return false;

        var normalizedEmail = email.Trim();

        // 1) Try PostgreSQL-backed authentication first
        UserAccount? account = null;
        try
        {
            account = await _db.UserAccounts
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail.ToLower());
        }
        catch (Exception ex)
        {
            // DB not ready yet — fall back to demo credentials
            _logger.LogWarning(ex, "DB lookup failed during sign-in; falling back to demo credentials");
        }

        // 2) If DB account exists, verify password (BCrypt-style: store hash in AuthUid for demo)
        if (account != null)
        {
            // For the seeded demo admin, we use the known password directly.
            // In a production app, you'd use BCrypt.Verify(password, account.PasswordHash).
            var isPasswordValid = account.AuthUid == "demo-admin" && password == AdminPassword
                || password == AdminPassword; // demo: accept Admin@2025 for all seeded accounts

            if (!isPasswordValid || account.Status == AccountStatus.Suspended || account.Status == AccountStatus.Disabled)
                return false;

            var displayName = account.FullName;
            var role = account.UserType == "owner" ? UserRole.SuperAdmin : account.Role;

            await IssueCookieAsync(
                userId: account.Id.ToString(),
                displayName: displayName,
                email: account.Email,
                role: role.StorageKey(),
                rememberMe: rememberMe);
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
            return true;
        }

        return false;
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
        await _http.HttpContext!.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public bool IsAuthenticated => _http.HttpContext?.User?.Identity?.IsAuthenticated == true;
    public string? DisplayName => _http.HttpContext?.User?.FindFirst(ClaimTypes.Name)?.Value;
    public string? Email => _http.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value;
}
