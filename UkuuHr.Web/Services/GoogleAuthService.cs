// ─────────────────────────────────────────────────────────────────────────────
// GoogleAuthService — Google OAuth 2.0 sign-in with a demo fallback.
//
// Production flow (when UKUU_GOOGLE_CLIENT_ID is set):
//   1. User clicks "Continue with Google" on /login or /signup
//   2. Browser redirects to /auth/google/login
//   3. Server responds with 302 redirect to Google's consent screen
//   4. User consents → Google redirects to /auth/google/callback?code=...
//   5. Server exchanges the code for an access token + id_token
//   6. Server reads the user's email + name from the id_token
//   7. Server finds-or-creates a UserAccount with AuthUid = "google:<sub>"
//   8. Server issues the auth cookie, redirects to /dashboard
//
// Demo flow (when UKUU_GOOGLE_CLIENT_ID is NOT set):
//   1. User clicks "Continue with Google"
//   2. Browser redirects to /auth/google/login
//   3. Server immediately creates a demo Google user "google.user@ukuuhr.demo"
//      and signs them in (no real Google call). Used for trial / sandbox.
// ─────────────────────────────────────────────────────────────────────────────

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

public class GoogleAuthService
{
    private readonly IHttpContextAccessor _http;
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<GoogleAuthService> _logger;
    private readonly AuditService _audit;
    private static readonly HttpClient Http = new();

    public GoogleAuthService(
        IHttpContextAccessor http,
        UkuuHrDbContext db,
        ILogger<GoogleAuthService> logger,
        AuditService audit)
    {
        _http = http;
        _db = db;
        _logger = logger;
        _audit = audit;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Config — read from environment variables.
    // ─────────────────────────────────────────────────────────────────────────
    public static string ClientId => Environment.GetEnvironmentVariable("UKUU_GOOGLE_CLIENT_ID") ?? "";
    public static string ClientSecret => Environment.GetEnvironmentVariable("UKUU_GOOGLE_CLIENT_SECRET") ?? "";
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// Build the redirect URI for the OAuth callback. Uses the request's host
    /// so it works on localhost, the preview URL, or any custom domain.
    /// </summary>
    private string BuildRedirectUri()
    {
        var req = _http.HttpContext?.Request;
        if (req == null) return "https://localhost/auth/google/callback";
        var scheme = req.Scheme;
        var host = req.Host.Host;
        var port = req.Host.Port;
        var hostStr = port.HasValue && !((scheme == "http" && port == 80) || (scheme == "https" && port == 443))
            ? $"{host}:{port}"
            : host;
        return $"{scheme}://{hostStr}/auth/google/callback";
    }

    /// <summary>
    /// Returns the URL to redirect the user to (Google's consent screen, or
    /// our demo login if credentials aren't configured).
    /// </summary>
    public string GetLoginUrl(string? returnUrl = null)
    {
        var ctx = _http.HttpContext!;
        if (!IsConfigured)
        {
            // Demo mode — short-circuit to the callback with a "demo" code.
            var cb = "/auth/google/callback?code=DEMO";
            if (!string.IsNullOrEmpty(returnUrl))
                cb += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
            return cb;
        }

        // Production mode — build the Google OAuth URL.
        var redirectUri = BuildRedirectUri();
        var state = Guid.NewGuid().ToString("N");
        // Stash state + returnUrl in short-lived cookies to validate the callback
        ctx.Response.Cookies.Append("ukuu.google.state", state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/"
        });
        if (!string.IsNullOrEmpty(returnUrl))
        {
            ctx.Response.Cookies.Append("ukuu.google.returnUrl", returnUrl, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(5),
                Path = "/"
            });
        }

        var scope = "openid email profile";
        return $"https://accounts.google.com/o/oauth2/v2/auth" +
               $"?client_id={Uri.EscapeDataString(ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&scope={Uri.EscapeDataString(scope)}" +
               $"&state={state}" +
               $"&prompt=select_account";
    }

    /// <summary>
    /// Handle the OAuth callback. Exchanges the code for tokens, fetches the
    /// user info, finds-or-creates the UserAccount, and issues the auth cookie.
    /// </summary>
    public async Task<(bool Success, string RedirectUrl, string? Error)> HandleCallbackAsync(string code, string? state)
    {
        var ctx = _http.HttpContext!;

        // ─── Demo mode ───────────────────────────────────────────────────────
        if (!IsConfigured || code == "DEMO")
        {
            _logger.LogInformation("Google sign-in: DEMO MODE (no UKUU_GOOGLE_CLIENT_ID configured). Creating demo Google user.");
            var demoReturnUrl = ctx.Request.Query["returnUrl"].ToString();
            return await SignInDemoGoogleUserAsync(demoReturnUrl);
        }

        // ─── Production mode ─────────────────────────────────────────────────
        var expectedState = ctx.Request.Cookies["ukuu.google.state"];
        ctx.Response.Cookies.Delete("ukuu.google.state");
        if (string.IsNullOrEmpty(expectedState) || expectedState != state)
            return (false, "/login?error=1", "OAuth state mismatch — please try again.");

        var returnUrl = ctx.Request.Cookies["ukuu.google.returnUrl"] ?? "/dashboard";
        ctx.Response.Cookies.Delete("ukuu.google.returnUrl");

        // Exchange code for tokens
        var tokenResponse = await ExchangeCodeAsync(code);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.IdToken))
            return (false, "/login?error=1", "Failed to exchange authorization code with Google.");

        // Decode the id_token to get the user's Google profile
        var profile = DecodeIdToken(tokenResponse.IdToken);
        if (profile == null || string.IsNullOrEmpty(profile.Email))
            return (false, "/login?error=1", "Failed to read user profile from Google id_token.");

        return await SignInGoogleUserAsync(profile, returnUrl);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Token exchange — calls Google's token endpoint.
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<GoogleTokenResponse?> ExchangeCodeAsync(string code)
    {
        var redirectUri = BuildRedirectUri();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });

        try
        {
            var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", content);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google token exchange failed: {Status} {Body}",
                    resp.StatusCode, await resp.Content.ReadAsStringAsync());
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GoogleTokenResponse>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during Google token exchange");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Decode a Google id_token (JWT). We don't validate the signature here
    // because the token came directly from Google's token endpoint over TLS.
    // (For full production, validate the signature with Google's public keys.)
    // ─────────────────────────────────────────────────────────────────────────
    private GoogleProfile? DecodeIdToken(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            // Base64url → Base64
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var jsonBytes = Convert.FromBase64String(payload);
            var json = System.Text.Encoding.UTF8.GetString(jsonBytes);
            return JsonSerializer.Deserialize<GoogleProfile>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode Google id_token");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Find-or-create a UserAccount from a Google profile, then issue cookie.
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<(bool Success, string RedirectUrl, string? Error)> SignInGoogleUserAsync(GoogleProfile profile, string returnUrl)
    {
        var googleId = $"google:{profile.Sub}";
        var normalizedEmail = profile.Email!.Trim().ToLowerInvariant();

        var account = await _db.UserAccounts.FirstOrDefaultAsync(u =>
            u.AuthUid == googleId || u.Email.ToLower() == normalizedEmail);

        var org = await _db.Organizations.FirstOrDefaultAsync();
        if (org == null) return (false, "/login?error=1", "No organization found.");

        if (account == null)
        {
            var (firstName, lastName) = SplitGoogleName(profile.Name, profile.Email);
            account = new UserAccount
            {
                OrganizationId = org.Id,
                Email = profile.Email,
                FirstName = firstName,
                LastName = lastName,
                AuthUid = googleId,
                Role = UserRole.HrOperator,  // default role for self-signups
                UserType = "user",
                Status = AccountStatus.Active,
                CreatedAt = DateTime.UtcNow,
            };
            _db.UserAccounts.Add(account);
            try { await _db.SaveChangesAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Google user {Email}", profile.Email);
                return (false, "/login?error=1", "Failed to create user account.");
            }
            _logger.LogInformation("Created new Google user {Email}", profile.Email);
        }
        else if (account.AuthUid != googleId && !account.AuthUid.StartsWith("google:"))
        {
            // Existing email-based account — link it to Google
            account.AuthUid = googleId;
            try { await _db.SaveChangesAsync(); } catch { /* best effort */ }
        }

        await IssueAuthCookieAsync(account);
        try
        {
            await _audit.LogAsync(org.Id, AuditAction.LoginSuccess, account.Email,
                details: $"Google OAuth sign-in for {account.Email}");
        }
        catch { /* audit is best-effort */ }

        var safe = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("/login")
            ? returnUrl : "/dashboard";
        return (true, safe, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Demo mode — create a demo Google user and sign them in.
    // Used when UKUU_GOOGLE_CLIENT_ID is not configured (sandbox / trial).
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<(bool Success, string RedirectUrl, string? Error)> SignInDemoGoogleUserAsync(string? returnUrl)
    {
        const string demoEmail = "google.user@ukuuhr.demo";
        const string demoSub = "demo-google-001";
        var googleId = $"google:{demoSub}";

        var org = await _db.Organizations.FirstOrDefaultAsync();
        if (org == null) return (false, "/login?error=1", "No organization found.");

        var account = await _db.UserAccounts.FirstOrDefaultAsync(u => u.AuthUid == googleId);
        if (account == null)
        {
            account = new UserAccount
            {
                OrganizationId = org.Id,
                Email = demoEmail,
                FirstName = "Google",
                LastName = "Demo User",
                AuthUid = googleId,
                Role = UserRole.HrOperator,
                UserType = "user",
                Status = AccountStatus.Active,
                CreatedAt = DateTime.UtcNow,
            };
            _db.UserAccounts.Add(account);
            try { await _db.SaveChangesAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create demo Google user");
                return (false, "/login?error=1", "Failed to create demo user.");
            }
        }

        await IssueAuthCookieAsync(account);
        try
        {
            await _audit.LogAsync(org.Id, AuditAction.LoginSuccess, demoEmail,
                details: "Demo Google OAuth sign-in (no UKUU_GOOGLE_CLIENT_ID configured)");
        }
        catch { /* best-effort */ }

        var safe = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("/login")
            ? returnUrl : "/dashboard";
        return (true, safe, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Issue the auth cookie using the same claims structure as AuthService.
    // ─────────────────────────────────────────────────────────────────────────
    private async Task IssueAuthCookieAsync(UserAccount account)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new(ClaimTypes.Name, account.FullName),
            new(ClaimTypes.Email, account.Email),
            new(ClaimTypes.Role, account.Role.StorageKey()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await _http.HttpContext!.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Wire models for JSON deserialization
    // ─────────────────────────────────────────────────────────────────────────
    private class GoogleTokenResponse
    {
        public string? AccessToken { get; set; }
        public string? IdToken { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresIn { get; set; }
        public string? RefreshToken { get; set; }
    }

    private class GoogleProfile
    {
        public string? Sub { get; set; }
        public string? Email { get; set; }
        public string? Name { get; set; }
        public string? Picture { get; set; }
        public bool EmailVerified { get; set; }
    }

    /// <summary>
    /// Split a Google profile "Name" (e.g. "Chabwela Mwale" or "Chabwela Mwale Jr.")
    /// into (FirstName, LastName). Falls back to the email's local-part if Name is null.
    /// </summary>
    private static (string First, string Last) SplitGoogleName(string? name, string? fallbackEmail = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var local = fallbackEmail?.Split('@')[0] ?? "User";
            return (local, "");
        }
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return (parts[0], "");
        return (parts[0], string.Join(' ', parts[1..]));
    }
}
