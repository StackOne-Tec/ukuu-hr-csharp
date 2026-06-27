using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace UkuuHr.Services;

/// <summary>
/// Simple cookie-based authentication service for demo.
/// Default credentials: admin@ukuuhr.demo / Admin@2025
/// </summary>
public class AuthService
{
    public const string AdminEmail = "admin@ukuuhr.demo";
    public const string AdminPassword = "Admin@2025";
    public const string AdminDisplayName = "Chungu Chama";

    private readonly IHttpContextAccessor _http;
    public AuthService(IHttpContextAccessor http) => _http = http;

    public async Task<bool> SignInAsync(string email, string password, bool rememberMe = false)
    {
        if (!string.Equals(email?.Trim(), AdminEmail, StringComparison.OrdinalIgnoreCase) || password != AdminPassword)
            return false;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-001"),
            new(ClaimTypes.Name, AdminDisplayName),
            new(ClaimTypes.Email, AdminEmail),
            new(ClaimTypes.Role, "SuperAdmin")
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
        return true;
    }

    public async Task SignOutAsync()
    {
        await _http.HttpContext!.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public bool IsAuthenticated => _http.HttpContext?.User?.Identity?.IsAuthenticated == true;
    public string? DisplayName => _http.HttpContext?.User?.FindFirst(ClaimTypes.Name)?.Value;
    public string? Email => _http.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value;
}
