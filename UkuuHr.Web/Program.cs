using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using Scalar.AspNetCore;
using System.Text;
using UkuuHr.Components;
using UkuuHr.Data;
using UkuuHr.Models;
using UkuuHr.Services;
using UkuuHr.Services.Devices;

// Use legacy timestamp behavior so DateTime is treated as 'timestamp without time zone'
// This avoids the "Cannot apply binary operation on types 'timestamp with time zone' and 'timestamp without time zone'" error
// when comparing DateTime properties with DateTime.Today/Now in LINQ queries.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ───────────── Config reload OFF (defense-in-depth vs Render inotify crash) ─────────────
// Render's shared containers cap inotify instances at 128 per user, and the
// default appsettings.json sources register a FileSystemWatcher (one inotify
// instance each) for runtime config reload. When the container runs as root
// (all root containers share one budget) this exhausts the limit and the app
// dies with "The configured user limit (128) on the number of inotify
// instances has been reached" (System.IO.IOException).
// The PRIMARY fix is in the Dockerfile: run as a non-root user (USER $APP_UID)
// so the app has its own inotify budget. Containers are immutable anyway, so
// file reload is pointless here — re-register the same sources with
// reloadOnChange: false (env vars + command line already have no watchers),
// preserving the original override order, so the app needs zero watchers.
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false);
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// ───────────── Database (PostgreSQL in prod, SQLite fallback for local dev) ─────────────
// Priority: explicit Npgsql connection string env var > DATABASE_URL (if postgres://) > SQLite local file
// When running in our Docker container, entrypoint.sh exports POSTGRES_CONNECTION_STRING pointing to localhost.
// When running locally without env vars set, falls back to a SQLite file in the project root.
// Note: DATABASE_URL from some environments (e.g. sandbox) may be a non-PostgreSQL URL (file://) —
// we only use it if it starts with "postgres://" to avoid Npgsql connection string parse errors.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var explicitConnStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("ConnectionString")
    ?? Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");

string connectionString;
bool useSqlite;
if (!string.IsNullOrWhiteSpace(explicitConnStr))
{
    connectionString = explicitConnStr;
    useSqlite = false;
}
else if (!string.IsNullOrWhiteSpace(databaseUrl) && databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
{
    connectionString = ConvertRenderDatabaseUrlToNpgsql(databaseUrl);
    useSqlite = false;
}
else
{
    // Local development fallback — SQLite file next to the project.
    // PostgreSQL is unavailable in some dev environments (e.g. sandboxed CI).
    var sqlitePath = builder.Configuration.GetConnectionString("SqlitePath") ?? "ukuuhr.db";
    connectionString = $"Data Source={sqlitePath}";
    useSqlite = true;
}

if (useSqlite)
{
    builder.Services.AddDbContext<UkuuHrDbContext>(options =>
        options.UseSqlite(connectionString)
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
}
else
{
    builder.Services.AddDbContext<UkuuHrDbContext>(options =>
        options.UseNpgsql(connectionString)
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
}

static string ConvertRenderDatabaseUrlToNpgsql(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        return url; // already a Npgsql-style connection string
    var userInfo = uri.UserInfo.Split(':', 2);
    var user = Uri.UnescapeDataString(userInfo[0]);
    var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
    var ssl = uri.Host.EndsWith(".render.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 5432;
    // P2/M-3: Removed TrustServerCertificate=true — use proper CA-verified cert for PostgreSQL
    return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={user};Password={pass};SSL Mode={(ssl ? "Require" : "Prefer")};Timeout=15;CommandTimeout=60";
}

// ExtractHost removed — was unused (CS8321)

// ───── Sanitize messages before embedding them in redirect query strings ─────
/// <summary>
/// Strips control characters, collapses whitespace, and caps the length of a
/// message so exception detail can never produce huge URLs or unexpected
/// characters when placed into a redirect query string.
/// </summary>
static string SanitizeRedirectMessage(string? message)
{
    if (string.IsNullOrWhiteSpace(message)) return "Unknown error.";
    var cleaned = string.Concat(message.Select(c => char.IsControl(c) ? ' ' : c));
    cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    const int maxLength = 200;
    return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "...";
}

// ───────────── Authentication ─────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/landing";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/landing";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "UkuuHr.Auth";
        options.Cookie.HttpOnly = true;
        // P2/M-1: Always send cookie over HTTPS in production (prevents proxy-induced HTTP downgrade).
        // Non-production environments keep the ASP.NET Core default (SameAsRequest) so local
        // HTTP development and WebApplicationFactory integration tests can authenticate.
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        // P2/M-2: SameSite=Strict — cookie only sent for same-site requests (strongest CSRF defense)
        options.Cookie.SameSite = SameSiteMode.Strict;
        // P2/M-7: Cap maximum session lifetime to 24 hours (prevent infinite sliding sessions)
        options.Events = new CookieAuthenticationEvents
        {
            OnSigningIn = context =>
            {
                // Enforce absolute maximum session lifetime of 24 hours
                var issued = context.Properties.IssuedUtc ?? DateTimeOffset.UtcNow;
                if (context.Properties.ExpiresUtc > issued.AddHours(24))
                    context.Properties.ExpiresUtc = issued.AddHours(24);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Role values match UserRole.StorageKey() — lowercase with underscores.
    // Phase 13.3: Fixed mismatch (previously checked "SuperAdmin" but claims store "super_admin").
    options.AddPolicy("AdminOnly", p => p.RequireRole(
        "super_admin", "hr_admin", "finance_payroll_admin", "hr_operator", "finance_payroll"));
    options.AddPolicy("SuperAdminOnly", p => p.RequireRole("super_admin"));
    options.AddPolicy("HrOrAdmin", p => p.RequireRole(
        "super_admin", "hr_admin", "hr_operator"));
    options.AddPolicy("FinanceOrAdmin", p => p.RequireRole(
        "super_admin", "finance_payroll_admin", "finance_payroll"));
    options.AddPolicy("UserManagement", p => p.RequireRole(
        "super_admin", "hr_admin"));
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, CookieAuthStateProvider>();
builder.Services.AddHttpContextAccessor();

// ───────────── Blazor ─────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddCircuitOptions(options =>
    {
        // P0/H-8: Only enable DetailedErrors in Development — prevent stack trace leaks in production
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });

// P0/C-1: One-time token service for POST-based auto-login (replaces credentials-in-URL)
builder.Services.AddSingleton<AutoLoginTokenService>();

// Phase 13.3: API key rate limit tracker (must be registered before Build())
builder.Services.AddSingleton<ApiKeyRateLimitTracker>();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.VisibleStateDuration = 4000;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 4;
});

// ───────────── OpenAPI / Swagger ─────────────
builder.Services.AddOpenApi();

// ───────────── App services ─────────────
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<EmployeeService>();
builder.Services.AddScoped<AttendanceService>();
builder.Services.AddScoped<LeaveService>();
builder.Services.AddScoped<PayrollService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<HikvisionSyncService>();
builder.Services.AddScoped<OvertimeService>();
builder.Services.AddScoped<TimeCardService>();
builder.Services.AddHttpClient("KeepAlive");
// Default HttpClient for Blazor pages that @inject HttpClient (e.g. Import From Device)
builder.Services.AddHttpClient();

// ───── Phase 1: FR-003 / FR-004 / FR-005 — Shifts & Tolerance ─────
builder.Services.AddScoped<ShiftService>();

// ───── Phase 2: FR-006 / FR-007 / FR-008 — Overtime & Holidays ─────
builder.Services.AddScoped<HolidayService>();

// ───── Phase 3: FR-001 — Multi-vendor device integration ─────
// Register all 7 vendor REST connectors + the shared CSV connector + SDK/TCP stubs.
builder.Services.AddScoped<UkuuHr.Services.Devices.HikvisionRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.ZKTecoRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.SupremaRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.DahuaRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.AnvizRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.MatrixRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.EsslRestConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.CsvConnector>();
// SDK + TCP stubs (return clear "install vendor SDK" error until overridden).
builder.Services.AddScoped<UkuuHr.Services.Devices.ZKTecoSdkConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.SupremaSdkConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.AnvizSdkConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.MatrixSdkConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.EsslSdkConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.DahuaSdkConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.SupremaTcpConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.MatrixTcpConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.EsslTcpConnector>();
builder.Services.AddScoped<UkuuHr.Services.Devices.AnvizTcpConnector>();

// Register the connector registry + orchestrator. The registry must be scoped:
// its factory resolves the scoped vendor connectors, so building it at singleton
// lifetime from the root provider throws under scope validation (dev/tests) and
// would otherwise leak scoped instances into singleton lifetime.
builder.Services.AddScoped<UkuuHr.Services.Devices.IDeviceConnectorRegistry>(sp =>
{
    var connectors = new List<UkuuHr.Services.Devices.IDeviceConnector>();
    // The CsvConnector is vendor-agnostic — register it for ALL vendors under the CsvFile mode.
    var csv = sp.GetRequiredService<UkuuHr.Services.Devices.CsvConnector>();
    foreach (var vendor in Enum.GetValues<UkuuHr.Models.DeviceVendor>())
    {
        // Create a vendor-specific wrapper.
        connectors.Add(new UkuuHr.Services.Devices.VendorSpecificCsvAdapter(csv, vendor));
    }
    // REST connectors — one per vendor.
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.HikvisionRestConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.ZKTecoRestConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.SupremaRestConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.DahuaRestConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.AnvizRestConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.MatrixRestConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.EsslRestConnector>());
    // SDK stubs.
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.ZKTecoSdkConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.SupremaSdkConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.AnvizSdkConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.MatrixSdkConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.EsslSdkConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.DahuaSdkConnector>());
    // TCP stubs.
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.SupremaTcpConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.MatrixTcpConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.EsslTcpConnector>());
    connectors.Add(sp.GetRequiredService<UkuuHr.Services.Devices.AnvizTcpConnector>());

    return new UkuuHr.Services.Devices.DeviceConnectorRegistry(connectors);
});
builder.Services.AddScoped<UkuuHr.Services.Devices.DeviceSyncOrchestrator>();

// ───── HikVision ISAPI Integration — World-class device integration ─────
builder.Services.AddScoped<UkuuHr.Services.Hikvision.HikvisionIsapiClient>(sp =>
{
    // Default client — will be re-created per-device by the connectors
    var config = new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = "localhost", Port = 80 };
    var logger = sp.GetRequiredService<ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>>();
    return new UkuuHr.Services.Hikvision.HikvisionIsapiClient(config, logger);
});
builder.Services.AddScoped<UkuuHr.Services.Hikvision.HikvisionEventProcessor>();
builder.Services.AddScoped<UkuuHr.Services.Hikvision.HikvisionDiagnosticService>();
builder.Services.AddHostedService<UkuuHr.Services.Hikvision.HikvisionBackgroundService>();

// ───── Phase 4: FR-009 Attendance Search + FR-010 Reporting ─────
builder.Services.AddScoped<AttendanceSearchService>();
builder.Services.AddScoped<ReportExportService>();

// ───── Phase 13.5: Encryption at rest ─────
builder.Services.AddScoped<AesEncryptionService>();

// ───── FR-013: Notifications module ─────
builder.Services.AddScoped<NotificationService>();

// ───── Transactional email (Resend) + subscription licensing ─────
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<LicenseService>();

// ───────────── KeepAlive: self-ping every 5 minutes to prevent Render free-tier spin-down ─────────────
builder.Services.AddHostedService<KeepAliveService>();

// ───── Phase 5: FR-002 — Automatic device sync background service ─────
builder.Services.AddHostedService<DeviceAutoSyncService>();

// ───────────── Background service fault tolerance ─────────────
// Without this, any unhandled exception in a BackgroundService (e.g. DeviceAutoSyncService
// timing out when a Hikvision device is unreachable) crashes the entire host.
// Setting Ignore keeps the host running — the failing service simply stops retrying.
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(opts =>
    opts.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);

var app = builder.Build();

// Multi-tenant org resolution — ambient HttpContext access for OrgResolution extensions
OrgResolution.Configure(app.Services.GetRequiredService<IHttpContextAccessor>());

// ───────────── Initialize DB ─────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UkuuHrDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        // Retry up to 5 times — PostgreSQL on Render may take a few seconds to be ready on cold start
        var retry = 0;
        while (true)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                // ───── Phase 30: Idempotent schema migration ─────
                // EnsureCreatedAsync() only creates tables if the DB doesn't exist; it does NOT
                // add new columns to existing tables. We add a small set of safe ALTER TABLE
                // statements here to bring legacy databases (e.g. the long-running Prisma
                // Postgres instance) up to the current model. Column existence is checked
                // first (IdempotentMigrationRunner) and a plain ADD COLUMN runs only when the
                // column is missing — each step is wrapped in try/catch so a failure is logged
                // and startup continues.
                await IdempotentMigrationRunner.RunIdempotentMigrationsAsync(db, logger, useSqlite);
                await DbSeeder.SeedAsync(db);
                logger.LogInformation("Database initialized & seeded.");
                break;
            }
            catch (Exception ex) when (retry < 5)
            {
                retry++;
                logger.LogWarning(ex, "DB init attempt {Retry} failed — retrying in 3s...", retry);
                await Task.Delay(3000);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize database after retries.");
    }
}

// ───────────── Middleware ─────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Render (and most cloud hosts) terminate TLS at the proxy — honor forwarded headers
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

// Phase 17: Cache-control headers to prevent stale assets.
// - HTML pages: no-cache (always revalidate)
// - CSS/JS with ?v= param: max-age=31536000 (1 year — immutable, versioned)
// - Blazor framework (_framework/*): no-cache (must always be fresh)
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.EndsWith(".css") || path.EndsWith(".js"))
    {
        // Versioned assets (have ?v= param) — cache for 1 year
        if (ctx.Request.QueryString.Value?.Contains("v=") == true)
            ctx.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        else
            ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
    }
    else if (path.Contains("/_framework/"))
    {
        // Blazor framework files — never cache
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    }
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = ctx =>
    {
        // Default: no-cache for all static files unless overridden above
        if (!ctx.Context.Response.Headers.ContainsKey("Cache-Control"))
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";

        // For downloadable executables, set Content-Disposition to trigger browser download
        var fileName = ctx.File.Name;
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("macOS-arm64", StringComparison.OrdinalIgnoreCase))
        {
            var downloadName = Path.GetFileName(fileName);
            ctx.Context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{downloadName}\"";
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=3600";
        }
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ───── Phase 13.3: API key auth for external integration endpoints ─────
// Supports TWO authentication modes for /api/* endpoints:
//
// 1. DATABASE API KEY (preferred): Validates X-API-Key header against
//    ApiKeyRecord table. Resolves the organization from the key, enforces
//    per-key rate limits and scope checks. Keys are SHA-256 hashed at rest.
//
// 2. ENV VAR FALLBACK (legacy): If UKUU_API_KEY env var is set, validates
//    the X-API-Key header against it using constant-time comparison.
//    Used for the UkuuBridge desktop app and backward compatibility.
//
// P1/H-3: All key comparisons use constant-time to prevent timing attacks.
// P1/H-4: If neither auth method succeeds, require cookie auth as minimum fallback.
//
// Rate limiting: Per-key rate limits are tracked in-memory via ApiKeyRateLimitTracker.
// The tracker is registered as a singleton and cleaned up every 5 minutes.

// Resolve the pre-registered singleton (registered before builder.Build())
var _rateLimitTracker = app.Services.GetRequiredService<ApiKeyRateLimitTracker>();

// Background cleanup for rate limit tracker
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromMinutes(5));
        _rateLimitTracker.Cleanup();
    }
});

// ───────────── API-key scope enforcement ─────────────
// Maps request path + method to the ApiKeyScope a DB API key must hold.
// Only applies to X-API-Key (DB) authentication — the server-configured
// UKUU_API_KEY env fallback is a trusted full-access bridge key, and cookie
// authentication is governed by endpoint role policies instead.
static ApiKeyScope? RequiredApiScope(string path, string method)
{
    var isWrite = !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
    var p = path.TrimEnd('/');

    if (p.StartsWith("/api/api-keys") || p.StartsWith("/api/super-admin") || p.StartsWith("/api/admin"))
        return ApiKeyScope.FullAccess;
    if (p.StartsWith("/api/subscription"))
        return ApiKeyScope.FullAccess;
    if (p.StartsWith("/api/employees") || p.StartsWith("/api/branches") || p.StartsWith("/api/documents"))
        return isWrite ? ApiKeyScope.WriteEmployees : ApiKeyScope.ReadEmployees;
    if (p.StartsWith("/api/attendance") || p.StartsWith("/api/overtime")
        || p.StartsWith("/api/shifts") || p.StartsWith("/api/reports") || p.StartsWith("/api/time-cards"))
        return isWrite ? ApiKeyScope.WriteAttendance : ApiKeyScope.ReadAttendance;
    if (p.StartsWith("/api/leave"))
        return isWrite ? ApiKeyScope.LeaveManagement : ApiKeyScope.ReadAttendance;
    if (p.StartsWith("/api/payroll"))
        return isWrite ? ApiKeyScope.WritePayroll : ApiKeyScope.ReadPayroll;
    if (p.StartsWith("/api/devices") || p.StartsWith("/api/hikvision"))
        return ApiKeyScope.DeviceManagement;
    // Unlisted routes (modules, metrics, notifications, downloads…): reads open to
    // any valid key, writes require full access.
    return isWrite ? ApiKeyScope.FullAccess : null;
}

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    // Allow public access to download endpoints (desktop app binaries)
    if (path.StartsWith("/api/downloads/", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        var providedKey = ctx.Request.Headers["X-API-Key"].ToString();

        // ── Try database API key first ──────────────────────────────────────
        if (!string.IsNullOrEmpty(providedKey))
        {
            var db = ctx.RequestServices.GetRequiredService<UkuuHrDbContext>();
            var keyHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(providedKey))).ToLowerInvariant();

            var keyRecord = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null);

            if (keyRecord != null && (keyRecord.ExpiresAt == null || keyRecord.ExpiresAt > DateTime.UtcNow))
            {
                // ── Rate limit check ────────────────────────────────────────
                if (_rateLimitTracker.IsRateLimited(keyRecord.Id, keyRecord.RateLimitPerMinute))
                {
                    ctx.Response.StatusCode = 429;
                    await ctx.Response.WriteAsync("{\"error\":\"Rate limit exceeded. Try again later.\"}");
                    return;
                }

                // ── Scope enforcement (P1): the key must hold the scope the route requires ──
                var requiredScope = RequiredApiScope(path, ctx.Request.Method);
                if (requiredScope.HasValue && !keyRecord.HasScope(requiredScope.Value))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync(
                        $"{{\"error\":\"API key '{keyRecord.Name}' lacks the required scope '{requiredScope.Value}'. Grant it on the API Keys settings page.\"}}");
                    return;
                }

                // ── Update usage stats (fire-and-forget to avoid blocking) ──
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
                        keyRecord.LastUsedAt = DateTime.UtcNow;
                        keyRecord.LastUsedIp = clientIp;
                        keyRecord.TotalRequestCount++;
                        await db.SaveChangesAsync();
                    }
                    catch { /* non-critical — don't fail the request */ }
                });

                // ── Create authenticated principal with org + scope claims ───
                var scopeClaims = keyRecord.Scopes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => new System.Security.Claims.Claim("api_scope", s))
                    .ToList();

                var claims = new List<System.Security.Claims.Claim>
                {
                    new(System.Security.Claims.ClaimTypes.Name, $"apikey:{keyRecord.KeyPrefix}"),
                    new(System.Security.Claims.ClaimTypes.NameIdentifier, keyRecord.Id.ToString()),
                    new("org_id", keyRecord.OrganizationId.ToString()),
                    new("auth_method", "ApiKey"),
                };
                claims.AddRange(scopeClaims);

                ctx.User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(claims, "ApiKey"));

                // Store the resolved org ID on HttpContext.Items so downstream
                // endpoints can use it instead of FirstOrDefaultAsync()
                ctx.Items["ResolvedOrgId"] = keyRecord.OrganizationId;
                ctx.Items["ApiKeyRecord"] = keyRecord;

                await next();
                return;
            }
        }

        // ── Fall back to UKUU_API_KEY env var (legacy) ──────────────────────
        var envApiKey = Environment.GetEnvironmentVariable("UKUU_API_KEY");
        if (!string.IsNullOrEmpty(envApiKey) && !string.IsNullOrEmpty(providedKey))
        {
            // P1/H-3: Constant-time comparison to prevent timing attacks
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(providedKey),
                    System.Text.Encoding.UTF8.GetBytes(envApiKey)))
            {
                // API key matches — create a generic identity for the request
                ctx.User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "api-client") },
                        "ApiKey"));
                await next();
                return;
            }
        }

        // ── Fall back to cookie auth ────────────────────────────────────────
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            // In Development, allow unauthenticated access for convenience
            if (!ctx.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Unauthorized. Provide a valid X-API-Key header or sign in via cookie.\"}");
                return;
            }
        }
    }
    await next();
});

// Public health endpoint — used by Render health check + KeepAlive self-ping + UptimeRobot
// P3/L-3: Removed db_host and env from public response (reconnaissance risk)
var startTime = DateTime.UtcNow;
app.MapGet("/health", () => Results.Ok(new {
    status = "ok",
    timestamp = DateTime.UtcNow,
    uptime_seconds = (DateTime.UtcNow - startTime).TotalSeconds
}));

// ───── Desktop app download endpoint ─────
// Serves self-contained executables from wwwroot/downloads/ with proper
// Content-Disposition (attachment) headers so the browser triggers a file
// download instead of navigating to the URL. Also sets the correct MIME type.
app.MapGet("/api/downloads/{filename}", (string filename, HttpContext ctx) =>
{
    // Sanitize: only allow known download file names (prevent path traversal)
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UkuuHr-Windows-x64.exe",
        "UkuuHr-macOS-arm64",
        "UkuuHr-macOS-x64",
        "UkuuHr-Linux-x64",
        "UkuuHr-macOS-arm64.dmg"
    };

    if (!allowed.Contains(filename))
        return Results.NotFound(new { error = "File not found." });

    var filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "downloads", filename);
    if (!File.Exists(filePath))
    {
        // Fallback: redirect to GitHub Releases if the file isn't on this server
        var githubBase = "https://github.com/StackOne-Tec/ukuu-hr-csharp/releases/latest/download/";
        return Results.Redirect(githubBase + filename);
    }

    // Determine content type
    var contentType = filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? "application/vnd.microsoft.portable-executable"
        : filename.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
            ? "application/x-apple-diskimage"
            : "application/octet-stream";

    return Results.File(filePath, contentType, filename, enableRangeProcessing: true);
}).AllowAnonymous();

// ───── Phase 13.6: Availability endpoints (99.9% uptime target) ─────

// Liveness — is the process running? (cheap, no DB check)
app.MapGet("/liveness", () => Results.Ok(new { status = "alive", timestamp = DateTime.UtcNow }));

// Readiness — is the app ready to serve requests? (includes DB connectivity check)
app.MapGet("/readiness", async (UkuuHrDbContext db) =>
{
    try
    {
        // Quick DB ping — can we connect + execute a trivial query?
        var canConnect = await db.Database.CanConnectAsync();
        if (canConnect)
            return Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow, db = "connected" });
        return Results.Json(new { status = "not_ready", timestamp = DateTime.UtcNow, db = "unreachable" },
            statusCode: 503);
    }
    catch (Exception)
    {
        // P2/M-4: Return generic error to client — don't leak exception details
        return Results.Json(new { status = "not_ready", timestamp = DateTime.UtcNow, db = "error" },
            statusCode: 503);
    }
});

// Direct POST handler for login form (uses /auth/login to avoid conflict with Blazor's /login page route)
// P3/L-4: Simple in-memory rate limiter for login (5 attempts per IP per minute)
// .DisableAntiforgery() is required because this is a plain HTML <form> POST without a Blazor-rendered antiforgery token.
var loginAttempts = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();
app.MapPost("/auth/login", async (HttpContext ctx, AuthService auth, ILogger<Program> logger) =>
{
    // P3/L-4: Rate limiting — max 5 login attempts per IP per minute
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var now = DateTime.UtcNow;
    var entry = loginAttempts.AddOrUpdate(clientIp,
        _ => (1, now),
        (_, old) => old.WindowStart < now.AddMinutes(-1) ? (1, now) : (old.Count + 1, old.WindowStart));
    if (entry.Count > 5)
    {
        logger.LogWarning("Login rate limit exceeded for IP {Ip}", clientIp);
        return Results.Redirect("/login?error=rate_limited");
    }

    var form = await ctx.Request.ReadFormAsync();
    var email = form["FormData.Email"].ToString();
    var password = form["FormData.Password"].ToString();
    var rememberMe = form["FormData.RememberMe"] == "true";
    var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();

    logger.LogInformation("Login POST: email={Email}, rememberMe={RememberMe}", email, rememberMe);

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/login?error=1");
    }

    var success = await auth.SignInAsync(email, password, rememberMe);
    logger.LogInformation("Login result for {Email}: {Success}", email, success);

    if (success)
    {
        // Redirect to the originally requested page, or default to /dashboard
        var redirectUrl = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("/login") && !returnUrl.StartsWith("/landing")
            ? returnUrl
            : "/dashboard";
        return Results.Redirect(redirectUrl);
    }
    return Results.Redirect("/login?error=1");
}).DisableAntiforgery();

app.MapGet("/logout", async (AuthService auth) =>
{
    await auth.SignOutAsync();
    return Results.Redirect("/landing");
});

// Direct POST handler for register form
app.MapPost("/auth/register", async (HttpContext ctx, ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var firstName = form["FormData.FirstName"].ToString();
    var lastName = form["FormData.LastName"].ToString();
    var email = form["FormData.Email"].ToString();
    logger.LogInformation("Register POST: firstName={FirstName}, email={Email}", firstName, email);
    return Results.Redirect("/login?registered=1");
}).DisableAntiforgery();

// Phase 18: Real signup endpoint — creates org + user account + signs in.
// This is a traditional HTTP POST (not Blazor) so it has a real HttpContext
// and can issue the auth cookie. The SignUp.razor page uses a plain HTML
// <form method="post" action="/auth/signup"> that posts here.
// .DisableAntiforgery() is required because this is a plain HTML <form> POST
// without a Blazor-rendered antiforgery token.
app.MapPost("/auth/signup", async (HttpContext ctx, UkuuHrDbContext db, AuthService auth, AuditService audit, ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var firstName = form["FormData.FirstName"].ToString().Trim();
    var lastName = form["FormData.LastName"].ToString().Trim();
    var email = form["FormData.Email"].ToString().Trim();
    var phone = form["FormData.Phone"].ToString().Trim();
    var orgName = form["FormData.OrganizationName"].ToString().Trim();
    var country = form["FormData.Country"].ToString().Trim();
    if (string.IsNullOrEmpty(country)) country = "Zambia";
    var industry = form["FormData.Industry"].ToString().Trim();
    var password = form["FormData.Password"].ToString();
    var confirmPassword = form["FormData.ConfirmPassword"].ToString();
    var agreed = form["FormData.Agreed"] == "true";

    logger.LogInformation("Signup POST: firstName={FirstName}, email={Email}, org={Org}", firstName, email, orgName);

    // Validate
    if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        return Results.Redirect("/signup?error=Name is required");
    if (string.IsNullOrWhiteSpace(email))
        return Results.Redirect("/signup?error=Email is required");
    if (string.IsNullOrWhiteSpace(orgName))
        return Results.Redirect("/signup?error=Organization name is required");
    if (password.Length < 8)
        return Results.Redirect("/signup?error=Password must be at least 8 characters");
    // P3/L-5: Server-side password complexity validation
    if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
        return Results.Redirect("/signup?error=Password must contain uppercase, lowercase, and a number");
    if (password != confirmPassword)
        return Results.Redirect("/signup?error=Passwords do not match");
    if (!agreed)
        return Results.Redirect("/signup?error=You must agree to the terms");

    // Check if email already exists
    var existing = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
    if (existing != null)
        return Results.Redirect("/signup?error=An account with this email already exists");

    try
    {
        // 1. Create Organization
        var org = new Organization
        {
            Name = orgName,
            Country = country,
            Currency = country switch { "Tanzania" => "TZS", "Malawi" => "MWK", _ => "ZMW" },
            Industry = string.IsNullOrWhiteSpace(industry) ? null : industry,
            OwnerUserId = "pending",
            PayrollConfigJson = "{}",
            CreatedAt = DateTime.UtcNow
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        // 2. Create UserAccount
        var passwordHash = AuthService.HashPassword(password);
        var userAccount = new UserAccount
        {
            OrganizationId = org.Id,
            AuthUid = passwordHash,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Role = UserRole.SuperAdmin,
            UserType = "owner",
            Status = AccountStatus.Active,
            IsFirstLogin = false,
            CreatedAt = DateTime.UtcNow,
            LastActivatedAt = DateTime.UtcNow
        };
        db.UserAccounts.Add(userAccount);
        await db.SaveChangesAsync();
        // Fix: set OwnerUserId AFTER SaveChangesAsync so userAccount.Id is populated
        org.OwnerUserId = userAccount.Id.ToString();
        await db.SaveChangesAsync();

        // 3. Audit log
        await audit.LogAsync(org.Id, AuditAction.UserCreated, email,
            details: $"Self-registration: {firstName} {lastName} created organization '{orgName}'");

        // 3b. Provision a 30-day Professional trial license so the new tenant is
        // immediately functional (billing prompts appear as the trial winds down).
        try
        {
            var licenses = ctx.RequestServices.GetRequiredService<LicenseService>();
            await licenses.ProvisionTrialAsync(org.Id, email);
        }
        catch (Exception licenseEx)
        {
            logger.LogWarning(licenseEx, "Trial license provisioning failed for org {OrgId}", org.Id);
        }

        // 4. Sign in
        var success = await auth.SignInAsync(email, password, rememberMe: true);
        if (success)
        {
            logger.LogInformation("Signup success — user {Email} signed in, redirecting to /dashboard", email);
            return Results.Redirect("/dashboard");
        }

        logger.LogWarning("Signup: account created but sign-in failed for {Email}, redirecting to /login", email);
        return Results.Redirect("/login?registered=1");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Signup error for {Email}: {Message}", email, ex.Message);
        // P2/M-4: Generic error message to user, detailed error in logs
        return Results.Redirect("/signup?error=Registration failed. Please try again.");
    }
}).DisableAntiforgery();

// Phase 18: Auto-login endpoint for Blazor Server flows that can't access HttpContext.
// After account creation (which happens in a Blazor event handler without HttpContext),
// the app redirects here with forceLoad=true. This endpoint HAS a real HttpContext,
// so it can call AuthService.SignInAsync() to issue the auth cookie, then redirect
// to the dashboard.
// P0/C-1 + P1/H-1 + P1/H-9: Auto-login uses one-time token (no credentials in URL).
// After signup, a short-lived random token is generated server-side. This endpoint
// exchanges the token for an authenticated session. The token is consumed on first
// use and expires after 5 minutes — safe to use in a GET URL (like email verify links).
app.MapGet("/auth/auto-login", async (HttpContext ctx, AuthService auth, AutoLoginTokenService tokenService, ILogger<Program> logger) =>
{
    var token = ctx.Request.Query["token"].ToString();
    var returnUrl = ctx.Request.Query["returnUrl"].ToString();

    // P1/H-9: Validate returnUrl is a relative path (prevent open redirect)
    if (string.IsNullOrEmpty(returnUrl)
        || !returnUrl.StartsWith("/")
        || returnUrl.StartsWith("//")
        || returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
        || returnUrl.StartsWith("/landing", StringComparison.OrdinalIgnoreCase))
    {
        returnUrl = "/dashboard";
    }

    logger.LogInformation("Auto-login: token provided={HasToken}", !string.IsNullOrEmpty(token));

    if (string.IsNullOrEmpty(token))
        return Results.Redirect("/login?error=1");

    // Validate the one-time token (consumes it atomically)
    var credentials = tokenService.ConsumeToken(token);
    if (credentials == null)
    {
        logger.LogWarning("Auto-login token invalid or expired");
        return Results.Redirect("/login?error=1");
    }

    var success = await auth.SignInAsync(credentials.Value.Email, credentials.Value.Password, rememberMe: true);
    if (success)
    {
        logger.LogInformation("Auto-login success for {Email}", credentials.Value.Email);
        return Results.Redirect(returnUrl);
    }

    logger.LogWarning("Auto-login failed for {Email}", credentials.Value.Email);
    return Results.Redirect("/login?error=1");
});

// ─────────────────────────────────────────────────────────────────────────────
// Modular Architecture — REST API endpoints for the 8 core modules
//
// Employee Management | Attendance Management | Shift Management
// Leave Management | Payroll Integration | Reporting
// Notifications | API Services
// ─────────────────────────────────────────────────────────────────────────────

// ═════════════════════════════════════════════════════════════════════════════
// MODULE 1: Employee Management
// ═════════════════════════════════════════════════════════════════════════════

// GET /api/employees — list all employees with optional filters
app.MapGet("/api/employees", async (
    EmployeeService svc,
    UkuuHrDbContext db,
    int? orgId,
    string? search,
    string? department,
    string? status) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    EmploymentStatus? statusFilter = status != null && Enum.TryParse<EmploymentStatus>(status, true, out var s)
        ? s : null;

    var employees = await svc.GetAllAsync(oid, search, department, statusFilter);
    return Results.Ok(new
    {
        total = employees.Count,
        organizationId = oid,
        employees = employees.Select(e => new
        {
            e.Id,
            e.EmployeeCode,
            e.FirstName,
            e.Surname,
            fullName = e.FullName,
            initials = e.Initials,
            e.JobTitle,
            e.Department,
            e.Email,
            e.Phone,
            status = e.StatusDisplay,
            statusCode = e.Status,
            e.EmploymentType,
            e.BasicSalary,
            e.Currency,
            e.GrossSalary,
            e.JoiningDate,
            e.CreatedAt
        })
    });
}).WithName("EmployeesList");

// GET /api/employees/{id} — get a single employee
app.MapGet("/api/employees/{id:int}", async (
    EmployeeService svc,
    UkuuHrDbContext db,
    int id,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var emp = await svc.GetAsync(oid, id);
    if (emp == null) return Results.NotFound(new { error = "Employee not found." });
    return Results.Ok(new
    {
        emp.Id,
        emp.EmployeeCode,
        emp.Title,
        emp.FirstName,
        emp.MiddleNames,
        emp.Surname,
        fullName = emp.FullName,
        initials = emp.Initials,
        emp.JobTitle,
        emp.Department,
        emp.Email,
        emp.Phone,
        status = emp.StatusDisplay,
        statusCode = emp.Status,
        emp.EmploymentType,
        emp.ContractType,
        emp.DateOfBirth,
        emp.Gender,
        emp.Nationality,
        emp.NationalIdentityNumber,
        emp.PassportNumber,
        emp.MaritalStatus,
        emp.StreetAddress,
        emp.City,
        emp.Country,
        emp.BasicSalary,
        emp.Currency,
        displayCurrency = emp.DisplayCurrency,
        emp.GrossSalary,
        emp.TotalAllowances,
        emp.HourlyRate,
        effectiveHourlyRate = emp.EffectiveHourlyRate,
        emp.BankName,
        emp.Branch,
        emp.AccountNumber,
        emp.MobileMoney,
        emp.BeneficiaryName,
        emp.Tpin,
        emp.NapsaNumber,
        emp.HealthInsuranceNumber,
        emp.JoiningDate,
        emp.ContractEndDate,
        emp.ReportingManagerName,
        emp.ProbationaryPeriodMonths,
        emp.HolidayEntitlementDays,
        emp.WorkHoursPerWeek,
        emp.CreatedAt,
        emp.UpdatedAt
    });
}).WithName("EmployeesGet");

// POST /api/employees — create a new employee
app.MapPost("/api/employees", async (
    HttpContext ctx,
    EmployeeService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<Employee>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    body.OrganizationId = oid;
    var created = await svc.CreateAsync(body);
    return Results.Created($"/api/employees/{created.Id}", new { id = created.Id, employeeCode = created.EmployeeCode });
}).WithName("EmployeesCreate");

// PUT /api/employees/{id} — update an existing employee
app.MapPut("/api/employees/{id:int}", async (
    HttpContext ctx,
    EmployeeService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var existing = await svc.GetAsync(oid, id);
    if (existing == null) return Results.NotFound(new { error = "Employee not found." });

    var body = await ctx.Request.ReadFromJsonAsync<Employee>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    body.Id = id;
    body.OrganizationId = oid;
    var updated = await svc.UpdateAsync(body);
    return Results.Ok(new { id = updated.Id, updatedAt = updated.UpdatedAt });
}).WithName("EmployeesUpdate");

// DELETE /api/employees/{id} — delete an employee
app.MapDelete("/api/employees/{id:int}", async (
    EmployeeService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var deleted = await svc.DeleteAsync(oid, id);
    if (!deleted) return Results.NotFound(new { error = "Employee not found." });
    return Results.Ok(new { status = "deleted" });
}).WithName("EmployeesDelete");

// GET /api/employees/stats — employee statistics
app.MapGet("/api/employees/stats", async (
    EmployeeService svc,
    UkuuHrDbContext db,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var total = await svc.CountAsync(oid);
    var active = await svc.CountByStatusAsync(oid, EmploymentStatus.Active);
    var probation = await svc.CountByStatusAsync(oid, EmploymentStatus.Probation);
    var inactive = await svc.CountByStatusAsync(oid, EmploymentStatus.Inactive);
    var terminated = await svc.CountByStatusAsync(oid, EmploymentStatus.Terminated);
    var totalPayroll = await svc.TotalPayrollAsync(oid);
    var byDepartment = await svc.ByDepartmentAsync(oid);

    return Results.Ok(new
    {
        total,
        active,
        probation,
        inactive,
        terminated,
        totalPayroll,
        byDepartment
    });
}).WithName("EmployeesStats");

// ═════════════════════════════════════════════════════════════════════════════
// MODULE 2: Attendance Management
// ═════════════════════════════════════════════════════════════════════════════

// GET /api/attendance — list attendance records for a date range
app.MapGet("/api/attendance", async (
    AttendanceService svc,
    UkuuHrDbContext db,
    int? orgId,
    string? from,
    string? to) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    DateTime? fromDate = DateTime.TryParse(from, out var fd) ? fd : DateTime.Today;
    DateTime? toDate = DateTime.TryParse(to, out var td) ? td : DateTime.Today;

    var records = await svc.ForRangeAsync(oid, fromDate.Value, toDate.Value);
    return Results.Ok(new
    {
        total = records.Count,
        from = fromDate.Value.ToString("yyyy-MM-dd"),
        to = toDate.Value.ToString("yyyy-MM-dd"),
        records = records.Select(a => new
        {
            a.Id,
            a.EmployeeId,
            a.EmployeeName,
            date = a.Date.ToString("yyyy-MM-dd"),
            dateKey = a.DateKey,
            a.CheckIn,
            a.CheckOut,
            checkInLabel = a.CheckInLabel,
            checkOutLabel = a.CheckOutLabel,
            a.Status,
            a.Source,
            a.WorkedHours,
            a.Notes,
            a.BreakMinutes
        })
    });
}).WithName("AttendanceList");

// GET /api/attendance/today — today's attendance
app.MapGet("/api/attendance/today", async (
    AttendanceService svc,
    UkuuHrDbContext db,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var today = DateTime.Today;
    var records = await svc.ForDateAsync(oid, today);
    var breakdown = await svc.BreakdownAsync(oid, today);

    return Results.Ok(new
    {
        date = today.ToString("yyyy-MM-dd"),
        total = records.Count,
        breakdown,
        records = records.Select(a => new
        {
            a.Id,
            a.EmployeeId,
            a.EmployeeName,
            checkInLabel = a.CheckInLabel,
            checkOutLabel = a.CheckOutLabel,
            a.Status,
            a.WorkedHours,
            a.Source
        })
    });
}).WithName("AttendanceToday");

// POST /api/attendance/clock-in — clock in an employee
app.MapPost("/api/attendance/clock-in", async (
    HttpContext ctx,
    AttendanceService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var employeeIdStr = ctx.Request.Query["employeeId"].FirstOrDefault();
    if (!int.TryParse(employeeIdStr, out var employeeId))
        return Results.BadRequest(new { error = "Provide employeeId as a query parameter." });

    var result = await svc.ClockAsync(oid, employeeId, clockIn: true);
    if (result == null) return Results.NotFound(new { error = "Employee not found." });

    return Results.Ok(new
    {
        status = "clocked_in",
        employeeId,
        checkIn = result.CheckIn,
        dateKey = result.DateKey
    });
}).WithName("AttendanceClockIn");

// POST /api/attendance/clock-out — clock out an employee
app.MapPost("/api/attendance/clock-out", async (
    HttpContext ctx,
    AttendanceService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var employeeIdStr = ctx.Request.Query["employeeId"].FirstOrDefault();
    if (!int.TryParse(employeeIdStr, out var employeeId))
        return Results.BadRequest(new { error = "Provide employeeId as a query parameter." });

    var result = await svc.ClockAsync(oid, employeeId, clockIn: false);
    if (result == null) return Results.NotFound(new { error = "Employee not found." });

    return Results.Ok(new
    {
        status = "clocked_out",
        employeeId,
        checkOut = result.CheckOut,
        dateKey = result.DateKey
    });
}).WithName("AttendanceClockOut");

// POST /api/attendance/clock — unified clock in/out from the Clock page form
// Reads employeeId + action from form body, calls ClockAsync, redirects back
app.MapPost("/api/attendance/clock", async (
    HttpContext ctx,
    AttendanceService svc,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var employeeIdStr = form["employeeId"].ToString();
    var action = form["action"].ToString();
    var clockIn = action == "in";

    logger.LogInformation("Clock POST: employeeId={EmployeeId}, action={Action}", employeeIdStr, action);

    if (!int.TryParse(employeeIdStr, out var employeeId))
        return Results.Redirect("/clock?result=error");

    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.Redirect("/clock?result=error");

    var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
    if (emp == null) return Results.Redirect("/clock?result=error");

    var result = await svc.ClockAsync(oid, employeeId, clockIn);
    if (result == null) return Results.Redirect("/clock?result=error");

    var actionLabel = clockIn ? "clocked in" : "clocked out";
    var empName = Uri.EscapeDataString(emp.FullName);
    logger.LogInformation("Clock success: {Name} {Action} at {Time}", emp.FullName, actionLabel, DateTime.UtcNow);

    return Results.Redirect($"/clock?result=success&name={empName}&action={actionLabel}");
}).WithName("AttendanceClockUnified");

// ═════════════════════════════════════════════════════════════════════════════
// MODULE 3: Shift Management
// ═════════════════════════════════════════════════════════════════════════════

// GET /api/shifts — list all shifts
app.MapGet("/api/shifts", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var shifts = await svc.GetAllShiftsAsync(oid, includeInactive: true);
    return Results.Ok(new
    {
        total = shifts.Count,
        shifts = shifts.Select(s => new
        {
            s.Id,
            s.Name,
            s.Description,
            kind = s.KindDisplay,
            kindCode = s.Kind,
            s.Color,
            startTime = s.TimeWindow,
            startMinutes = s.StartMinutes,
            endMinutes = s.EndMinutes,
            s.BreakMinutes,
            plannedHours = s.PlannedHours,
            plannedWorkedHours = s.PlannedWorkedHours,
            s.IsOvernight,
            daysDisplay = s.DaysDisplay,
            s.IsActive,
            s.RotationCycleDays,
            s.RotationSlots,
            s.CreatedAt
        })
    });
}).WithName("ShiftsList");

// GET /api/shifts/{id} — get a single shift
app.MapGet("/api/shifts/{id:int}", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int id,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var shift = await svc.GetShiftAsync(oid, id);
    if (shift == null) return Results.NotFound(new { error = "Shift not found." });
    return Results.Ok(new
    {
        shift.Id,
        shift.Name,
        shift.Description,
        kind = shift.KindDisplay,
        kindCode = shift.Kind,
        shift.Color,
        startMinutes = shift.StartMinutes,
        endMinutes = shift.EndMinutes,
        timeWindow = shift.TimeWindow,
        plannedHours = shift.PlannedHours,
        plannedWorkedHours = shift.PlannedWorkedHours,
        shift.IsOvernight,
        shift.BreakMinutes,
        daysDisplay = shift.DaysDisplay,
        shift.DaysOfWeekMask,
        shift.IsActive,
        shift.RotationCycleDays,
        shift.RotationSlots,
        shift.FlexibleMinHours,
        shift.FlexibleMaxHours,
        shift.FlexibleCoreStartMinutes,
        shift.FlexibleCoreEndMinutes
    });
}).WithName("ShiftsGet");

// POST /api/shifts — create a new shift
app.MapPost("/api/shifts", async (
    HttpContext ctx,
    ShiftService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<Shift>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    var created = await svc.CreateShiftAsync(oid, body, actorEmail: null);
    return Results.Created($"/api/shifts/{created.Id}", new { id = created.Id, name = created.Name });
}).WithName("ShiftsCreate");

// PUT /api/shifts/{id} — update an existing shift
app.MapPut("/api/shifts/{id:int}", async (
    HttpContext ctx,
    ShiftService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<Shift>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    body.Id = id;
    var updated = await svc.UpdateShiftAsync(oid, body, actorEmail: null);
    return Results.Ok(new { id = updated.Id, updatedAt = updated.UpdatedAt });
}).WithName("ShiftsUpdate");

// DELETE /api/shifts/{id} — soft-delete (deactivate) a shift
app.MapDelete("/api/shifts/{id:int}", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var deleted = await svc.DeleteShiftAsync(oid, id, actorEmail: null);
    if (!deleted) return Results.NotFound(new { error = "Shift not found." });
    return Results.Ok(new { status = "deactivated" });
}).WithName("ShiftsDelete");

// ───── POST /api/shifts/delete/{id} — delete shift via form POST (works without Blazor circuit) ─────
app.MapPost("/api/shifts/delete/{id:int}", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var deleted = await svc.DeleteShiftAsync(oid, id, actorEmail: "admin@ukuuhr.demo");
    if (!deleted) return Results.NotFound(new { error = "Shift not found." });
    return Results.Redirect("/shifts?deleted=1");
}).WithName("ShiftsDeleteForm"); // P1/H-7: CSRF re-enabled

// POST /api/shifts/tolerance — save attendance tolerance policy (traditional form POST)
app.MapPost("/api/shifts/tolerance", async (
    HttpContext ctx,
    ShiftService svc,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.Redirect("/shifts/tolerance");

    var form = await ctx.Request.ReadFormAsync();
    var tolerance = await svc.GetOrCreateToleranceAsync(oid);

    tolerance.LateCheckInToleranceMinutes = int.TryParse(form["LateCheckInToleranceMinutes"], out var v1) ? v1 : 15;
    tolerance.VeryLateThresholdMinutes = int.TryParse(form["VeryLateThresholdMinutes"], out var v2) ? v2 : 60;
    tolerance.EarlyCheckOutToleranceMinutes = int.TryParse(form["EarlyCheckOutToleranceMinutes"], out var v3) ? v3 : 10;
    tolerance.HalfDayEarlyThresholdMinutes = int.TryParse(form["HalfDayEarlyThresholdMinutes"], out var v4) ? v4 : 180;
    tolerance.EarlyArrivalAllowanceMinutes = int.TryParse(form["EarlyArrivalAllowanceMinutes"], out var v5) ? v5 : 30;
    tolerance.CapEarlyArrivalToAllowance = form["CapEarlyArrivalToAllowance"] == "true";
    tolerance.MinPresentMinutesForAttendance = int.TryParse(form["MinPresentMinutesForAttendance"], out var v6) ? v6 : 240;
    tolerance.AutoMarkAbsentWhenNoClockEvent = form["AutoMarkAbsentWhenNoClockEvent"] == "true";
    tolerance.GracePeriodMinutes = int.TryParse(form["GracePeriodMinutes"], out var v7) ? v7 : 0;
    tolerance.GracePeriodDaysMask = int.TryParse(form["GracePeriodDaysMask"], out var v8) ? v8 : 31;
    tolerance.DefaultBreakMinutes = int.TryParse(form["DefaultBreakMinutes"], out var v9) ? v9 : 60;
    tolerance.MinWorkedMinutesBeforeBreak = int.TryParse(form["MinWorkedMinutesBeforeBreak"], out var v10) ? v10 : 240;
    tolerance.HalfDayWorkedMinutes = int.TryParse(form["HalfDayWorkedMinutes"], out var v11) ? v11 : 240;
    tolerance.UpdatedByEmail = "admin";
    tolerance.UpdatedAt = DateTime.UtcNow;

    try
    {
        await svc.UpdateToleranceAsync(oid, tolerance, "admin");
        logger.LogInformation("Tolerance policy saved for org {OrgId}", oid);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save tolerance policy for org {OrgId}", oid);
    }

    return Results.Redirect("/shifts/tolerance?saved=1");
}).WithName("ShiftsToleranceSave");

// GET /api/shifts/assignments — list shift assignments
app.MapGet("/api/shifts/assignments", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int? orgId,
    int? employeeId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var assignments = await svc.GetAssignmentsAsync(oid, employeeId);
    return Results.Ok(new
    {
        total = assignments.Count,
        assignments = assignments.Select(a => new
        {
            a.Id,
            a.EmployeeId,
            employeeName = a.Employee?.FullName,
            a.ShiftId,
            shiftName = a.Shift?.Name,
            shiftKind = a.Shift?.KindDisplay,
            a.IsPrimary,
            a.IsActive,
            effectiveFrom = a.EffectiveFrom.ToString("yyyy-MM-dd"),
            effectiveTo = a.EffectiveTo?.ToString("yyyy-MM-dd"),
            a.RotationSlot,
            a.CreatedAt
        })
    });
}).WithName("ShiftsAssignmentsList");

// POST /api/shifts/assignments — create a shift assignment
app.MapPost("/api/shifts/assignments", async (
    HttpContext ctx,
    ShiftService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<EmployeeShiftAssignment>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    var created = await svc.AssignShiftAsync(oid, body, actorEmail: null);
    return Results.Created($"/api/shifts/assignments/{created.Id}", new { id = created.Id });
}).WithName("ShiftsAssignmentsCreate");

// DELETE /api/shifts/assignments/{id} — remove a shift assignment
app.MapDelete("/api/shifts/assignments/{id:int}", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var deleted = await svc.UnassignShiftAsync(oid, id, actorEmail: null);
    if (!deleted) return Results.NotFound(new { error = "Assignment not found." });
    return Results.Ok(new { status = "removed" });
}).WithName("ShiftsAssignmentsDelete");

// GET /api/shifts/tolerance — get attendance tolerance config
app.MapGet("/api/shifts/tolerance", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var tolerance = await svc.GetOrCreateToleranceAsync(oid);
    return Results.Ok(new
    {
        tolerance.Id,
        lateCheckInToleranceMinutes = tolerance.LateCheckInToleranceMinutes,
        veryLateThresholdMinutes = tolerance.VeryLateThresholdMinutes,
        earlyCheckOutToleranceMinutes = tolerance.EarlyCheckOutToleranceMinutes,
        halfDayEarlyThresholdMinutes = tolerance.HalfDayEarlyThresholdMinutes,
        earlyArrivalAllowanceMinutes = tolerance.EarlyArrivalAllowanceMinutes,
        capEarlyArrivalToAllowance = tolerance.CapEarlyArrivalToAllowance,
        minPresentMinutesForAttendance = tolerance.MinPresentMinutesForAttendance,
        autoMarkAbsentWhenNoClockEvent = tolerance.AutoMarkAbsentWhenNoClockEvent,
        gracePeriodMinutes = tolerance.GracePeriodMinutes,
        defaultBreakMinutes = tolerance.DefaultBreakMinutes,
        halfDayWorkedMinutes = tolerance.HalfDayWorkedMinutes
    });
}).WithName("ShiftsToleranceGet");

// PUT /api/shifts/tolerance — update attendance tolerance config
app.MapPut("/api/shifts/tolerance", async (
    HttpContext ctx,
    ShiftService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<AttendanceTolerance>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    var updated = await svc.UpdateToleranceAsync(oid, body, actorEmail: null);
    return Results.Ok(new { id = updated.Id, updatedAt = updated.UpdatedAt });
}).WithName("ShiftsToleranceUpdate");

// ═════════════════════════════════════════════════════════════════════════════
// MODULE 4: Leave Management
// ═════════════════════════════════════════════════════════════════════════════

// GET /api/leave — list leave requests
app.MapGet("/api/leave", async (
    LeaveService svc,
    UkuuHrDbContext db,
    int? orgId,
    string? status,
    int? employeeId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    LeaveRequestStatus? statusFilter = status != null && Enum.TryParse<LeaveRequestStatus>(status, true, out var s)
        ? s : null;

    List<LeaveRequest> requests;
    if (employeeId.HasValue)
        requests = await svc.ForEmployeeAsync(oid, employeeId.Value);
    else
        requests = await svc.AllAsync(oid, statusFilter);

    return Results.Ok(new
    {
        total = requests.Count,
        requests = requests.Select(r => new
        {
            r.Id,
            r.EmployeeId,
            r.EmployeeName,
            r.LeaveTypeId,
            leaveType = r.LeaveTypeName,
            startDate = r.StartDate.ToString("yyyy-MM-dd"),
            endDate = r.EndDate.ToString("yyyy-MM-dd"),
            requestedDays = r.RequestedDays,
            r.Reason,
            status = r.StatusDisplay,
            statusCode = r.Status,
            r.ReviewedByEmail,
            reviewedAt = r.ReviewedAt?.ToString("yyyy-MM-dd HH:mm"),
            r.RejectionReason,
            r.ApproverNotes,
            createdAt = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            periodLabel = r.PeriodLabel
        })
    });
}).WithName("LeaveList");

// GET /api/leave/{id} — get a single leave request
app.MapGet("/api/leave/{id:int}", async (
    LeaveService svc,
    UkuuHrDbContext db,
    int id,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var lr = await svc.GetAsync(oid, id);
    if (lr == null) return Results.NotFound(new { error = "Leave request not found." });

    return Results.Ok(new
    {
        lr.Id,
        lr.EmployeeId,
        lr.EmployeeName,
        lr.LeaveTypeId,
        leaveType = lr.LeaveTypeName,
        startDate = lr.StartDate.ToString("yyyy-MM-dd"),
        endDate = lr.EndDate.ToString("yyyy-MM-dd"),
        requestedDays = lr.RequestedDays,
        lr.Reason,
        status = lr.StatusDisplay,
        statusCode = lr.Status,
        lr.IsExceptional,
        lr.DeductibleDays,
        lr.HolidayDays,
        lr.ReviewedByEmail,
        reviewedAt = lr.ReviewedAt?.ToString("yyyy-MM-dd HH:mm"),
        lr.RejectionReason,
        lr.ApproverNotes,
        createdAt = lr.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        periodLabel = lr.PeriodLabel
    });
}).WithName("LeaveGet");

// POST /api/leave — create a new leave request
app.MapPost("/api/leave", async (
    HttpContext ctx,
    LeaveService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<LeaveRequest>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    body.OrganizationId = oid;
    var created = await svc.CreateAsync(body);
    return Results.Created($"/api/leave/{created.Id}", new { id = created.Id, status = created.Status });
}).WithName("LeaveCreate");

// POST /api/leave/{id}/approve — approve a leave request (form POST or JSON)
app.MapPost("/api/leave/{id:int}/approve", async (
    HttpContext ctx,
    LeaveService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    // Identity of the authenticated reviewer (cookie or API-key principal).
    var reviewerEmail = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "admin@ukuuhr.demo";
    string? notes = null;
    if (ctx.Request.ContentType?.Contains("application/json") == true)
    {
        var body = await ctx.Request.ReadFromJsonAsync<ApprovalBody>();
        reviewerEmail = body?.ReviewerEmail ?? reviewerEmail;
        notes = body?.Notes;
    }

    var result = await svc.ReviewAsync(oid, id, approve: true, reviewerEmail, notes);
    if (!result) return Results.NotFound(new { error = "Leave request not found." });
    return Results.Redirect("/leave?tab=approved&reviewed=1");
}).WithName("LeaveApprove"); // P1/H-7: CSRF re-enabled

// POST /api/leave/{id}/reject — reject a leave request (form POST or JSON)
app.MapPost("/api/leave/{id:int}/reject", async (
    HttpContext ctx,
    LeaveService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    // Identity of the authenticated reviewer (cookie or API-key principal).
    var reviewerEmail = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "admin@ukuuhr.demo";
    string? notes = null;
    if (ctx.Request.ContentType?.Contains("application/json") == true)
    {
        var body = await ctx.Request.ReadFromJsonAsync<ApprovalBody>();
        reviewerEmail = body?.ReviewerEmail ?? reviewerEmail;
        notes = body?.Notes;
    }

    var result = await svc.ReviewAsync(oid, id, approve: false, reviewerEmail, notes);
    if (!result) return Results.NotFound(new { error = "Leave request not found." });
    return Results.Redirect("/leave?tab=rejected&reviewed=1");
}).WithName("LeaveReject"); // P1/H-7: CSRF re-enabled

// ───── POST /api/overtime/{id}/approve — approve overtime (form POST) ─────
app.MapPost("/api/overtime/{id:int}/approve", async (
    HttpContext ctx,
    OvertimeService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });
    var approver = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "admin@ukuuhr.demo";
    await svc.ApproveAsync(oid, id, approver, "Approved.");
    return Results.Redirect("/overtime?tab=approved&reviewed=1");
}).WithName("OvertimeApprove"); // P1/H-7: CSRF re-enabled

// ───── POST /api/overtime/{id}/reject — reject overtime (form POST) ─────
app.MapPost("/api/overtime/{id:int}/reject", async (
    HttpContext ctx,
    OvertimeService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });
    var rejector = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "admin@ukuuhr.demo";
    await svc.RejectAsync(oid, id, rejector, "Rejected.");
    return Results.Redirect("/overtime?tab=pending&reviewed=1");
}).WithName("OvertimeReject"); // P1/H-7: CSRF re-enabled

// ───── POST /api/overtime/auto-calculate ─────
app.MapPost("/api/overtime/auto-calculate", async (
    OvertimeService svc,
    UkuuHrDbContext db) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });
    var from = DateTime.Today.AddDays(-30);
    var to = DateTime.Today;
    var count = await svc.AutoCalculateForDateRangeAsync(oid, from, to);
    return Results.Redirect($"/overtime?tab=all&calculated={count}");
}).WithName("OvertimeAutoCalculate"); // P1/H-7: CSRF re-enabled

// POST /api/leave/{id}/cancel — cancel a leave request
app.MapPost("/api/leave/{id:int}/cancel", async (
    LeaveService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = await db.ResolveOrgIdAsync(); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var result = await svc.CancelAsync(oid, id);
    if (!result) return Results.NotFound(new { error = "Leave request not found or already reviewed." });
    return Results.Ok(new { status = "cancelled", id });
}).WithName("LeaveCancel");

// GET /api/leave/types — list leave types
app.MapGet("/api/leave/types", async (
    LeaveService svc,
    UkuuHrDbContext db,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var types = await svc.GetLeaveTypesAsync(oid);
    return Results.Ok(new
    {
        total = types.Count,
        types = types.Select(t => new
        {
            t.Id,
            t.Name,
            t.Color,
            t.DefaultDays,
            t.IsPaid,
            t.RequiresApproval,
            t.CarryForward,
            t.MaxCarryForwardDays
        })
    });
}).WithName("LeaveTypesList");

// GET /api/leave/balances — get leave balances for an employee
app.MapGet("/api/leave/balances", async (
    LeaveService svc,
    UkuuHrDbContext db,
    int? orgId,
    int? employeeId,
    int? year) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });
    if (!employeeId.HasValue) return Results.BadRequest(new { error = "employeeId is required." });

    var balances = await svc.GetOrCreateBalancesAsync(oid, employeeId.Value, year);
    return Results.Ok(new
    {
        year = year ?? DateTime.UtcNow.Year,
        balances = balances.Select(b => new
        {
            b.Id,
            b.LeaveTypeId,
            leaveType = b.LeaveType?.Name,
            b.Year,
            b.EntitlementDays,
            b.UsedDays,
            b.CarriedForwardDays,
            b.AdjustedDays,
            remainingDays = b.RemainingDays
        })
    });
}).WithName("LeaveBalancesGet");

// ─────────────────────────────────────────────────────────────────────────────
// Phase 5: FR-012 Payroll Integration API + FR-013 Modular API surface
//
// These endpoints expose attendance + leave + overtime data in JSON + CSV
// formats for external payroll systems (Sage, Xero, QuickBooks, custom ERP).
// They are intentionally RESTful and stateless so any payroll system can
// poll them on its own schedule.
// ─────────────────────────────────────────────────────────────────────────────

// FR-012: Payroll-ready attendance summary for a given month.
// Returns: per-employee { workedHours, overtimeHours, leaveDays, absentDays, status }
app.MapGet("/api/payroll/attendance-summary", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    int? orgId,
    int? year,
    int? month) =>
{
    var today = DateTime.Today;
    var y = year ?? today.Year;
    var m = month ?? today.Month;
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var from = new DateTime(y, m, 1);
    var to = from.AddMonths(1).AddTicks(-1);

    var attendance = await db.Attendances
        .Where(a => a.OrganizationId == oid && a.Date >= from && a.Date <= to)
        .ToListAsync();
    var overtime = await db.OvertimeRecords
        .Where(o => o.OrganizationId == oid && o.Date >= from && o.Date <= to
                 && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved)) // approved OT only — payroll-ready
        .ToListAsync();
    var leave = await db.LeaveRequests
        .Where(l => l.OrganizationId == oid && l.Status == LeaveRequestStatus.Approved
                 && l.StartDate <= to && l.EndDate >= from)
        .ToListAsync();
    var employees = await db.Employees
        .Where(e => e.OrganizationId == oid && e.Status != EmploymentStatus.Inactive)
        .ToListAsync();

    var rows = employees.Select(e =>
    {
        var empAttendance = attendance.Where(a => a.EmployeeId == e.Id).ToList();
        var empOt = overtime.Where(o => o.EmployeeId == e.Id).ToList();
        var empLeave = leave.Where(l => l.EmployeeId == e.Id).ToList();
        var leaveDays = empLeave.Sum(l => LeaveRequest.CalculateBusinessDays(
            l.StartDate < from ? from : l.StartDate,
            l.EndDate > to ? to : l.EndDate));
        return new
        {
            employeeId = e.Id,
            employeeCode = e.EmployeeCode,
            employeeName = e.FullName,
            department = e.Department,
            workedHours = Math.Round(empAttendance.Sum(a => a.WorkedHours), 2),
            overtimeHours = Math.Round(empOt.Sum(o => o.Hours), 2),
            overtimePay = Math.Round(empOt.Sum(o => o.Pay), 2),
            leaveDays,
            absentDays = empAttendance.Count(a => a.Status == AttendanceStatus.Absent),
            lateDays = empAttendance.Count(a => a.Status == AttendanceStatus.Late),
            halfDays = empAttendance.Count(a => a.Status == AttendanceStatus.HalfDay),
            basicSalary = e.BasicSalary,
            currency = e.DisplayCurrency
        };
    }).ToList();

    return Results.Ok(new
    {
        period = $"{y:0000}-{m:00}",
        organization = (await db.Organizations.FirstOrDefaultAsync(o => o.Id == oid))?.Name,
        generatedAt = DateTime.UtcNow,
        totalEmployees = rows.Count,
        rows
    });
}).WithName("PayrollAttendanceSummary");

// FR-012: Export attendance summary as CSV (for legacy payroll systems).
app.MapGet("/api/payroll/attendance-summary.csv", async (
    UkuuHrDbContext db,
    int? orgId,
    int? year,
    int? month) =>
{
    var today = DateTime.Today;
    var y = year ?? today.Year;
    var m = month ?? today.Month;
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound();

    var from = new DateTime(y, m, 1);
    var to = from.AddMonths(1).AddTicks(-1);
    var attendance = await db.Attendances
        .Where(a => a.OrganizationId == oid && a.Date >= from && a.Date <= to)
        .ToListAsync();
    var employees = await db.Employees
        .Where(e => e.OrganizationId == oid && e.Status != EmploymentStatus.Inactive)
        .ToListAsync();

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("EmployeeCode,EmployeeName,Department,WorkedHours,AbsentDays,LateDays,HalfDays,BasicSalary,Currency");
    foreach (var e in employees)
    {
        var att = attendance.Where(a => a.EmployeeId == e.Id).ToList();
        sb.AppendLine(string.Join(",",
            e.EmployeeCode ?? "",
            $"\"{e.FullName}\"",
            e.Department ?? "",
            att.Sum(a => a.WorkedHours).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            att.Count(a => a.Status == AttendanceStatus.Absent).ToString(),
            att.Count(a => a.Status == AttendanceStatus.Late).ToString(),
            att.Count(a => a.Status == AttendanceStatus.HalfDay).ToString(),
            e.BasicSalary.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            e.DisplayCurrency));
    }
    var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    return Results.File(bytes, "text/csv", $"attendance-summary-{y}{m:00}.csv");
}).WithName("PayrollAttendanceCsv");

// FR-013: Modular API — list of available modules + their status.
app.MapGet("/api/modules", async (UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    var modules = new List<ModuleInfo>
    {
        new("employees", "Employee Management", true, "GET /api/employees, /api/employees/stats, /api/employees/{id}"),
        new("attendance", "Attendance Management", true, "GET /api/attendance, /api/attendance/today, POST /api/attendance/clock-in, /api/attendance/clock-out"),
        new("shifts", "Shift Management", true, "GET /api/shifts, /api/shifts/{id}, /api/shifts/assignments, /api/shifts/tolerance"),
        new("leave", "Leave Management", true, "GET /api/leave, /api/leave/types, /api/leave/balances, POST /api/leave, /api/leave/{id}/approve"),
        new("payroll", "Payroll Integration", true, "GET /api/payroll/attendance-summary, /api/payroll/attendance-summary.csv"),
        new("reporting", "Reporting", true, "GET /api/reports/attendance/csv, /api/reports/attendance/xlsx, /api/reports/attendance/csv/search"),
        new("notifications", "Notifications", true, "GET /api/notifications, POST /api/notifications/{id}/read, /api/notifications/read-all"),
        new("devices", "Device Integration", true, "GET /api/devices")
    };
    return Results.Ok(new { organization = org?.Name, modules });
}).WithName("ModulesList");

// FR-013: Notifications API endpoints
app.MapGet("/api/notifications", async (
    UkuuHrDbContext db,
    int? orgId,
    string? userId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var q = db.NotificationRecords.Where(n => n.OrganizationId == oid);
    if (!string.IsNullOrWhiteSpace(userId))
        q = q.Where(n => n.RecipientUserId == null || n.RecipientUserId == userId);
    else
        q = q.Where(n => n.RecipientUserId == null);

    var total = await q.CountAsync();
    var unread = await q.CountAsync(n => !n.IsRead);
    var notifications = await q
        .OrderByDescending(n => n.CreatedAt)
        .Take(50)
        .Select(n => new
        {
            n.Id,
            n.Type,
            n.Title,
            n.Body,
            n.ActionUrl,
            n.ActionLabel,
            n.SourceModule,
            n.IsRead,
            n.ReadAt,
            n.CreatedAt,
            n.DeliveryStatus
        })
        .ToListAsync();

    return Results.Ok(new { total, unread, notifications });
}).WithName("NotificationsList");

// Mark a notification as read
app.MapPost("/api/notifications/{id:int}/read", async (
    UkuuHrDbContext db,
    int id,
    int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var n = await db.NotificationRecords
        .FirstOrDefaultAsync(x => x.OrganizationId == oid && x.Id == id);
    if (n == null) return Results.NotFound();

    n.IsRead = true;
    n.ReadAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { status = "ok" });
}).WithName("NotificationMarkRead");

// Mark all notifications as read
app.MapPost("/api/notifications/read-all", async (
    UkuuHrDbContext db,
    int? orgId,
    string? userId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var q = db.NotificationRecords.Where(n => n.OrganizationId == oid && !n.IsRead);
    if (!string.IsNullOrWhiteSpace(userId))
        q = q.Where(n => n.RecipientUserId == null || n.RecipientUserId == userId);
    else
        q = q.Where(n => n.RecipientUserId == null);

    var now = DateTime.UtcNow;
    var count = await q.CountAsync();
    await q.ExecuteUpdateAsync(s => s
        .SetProperty(n => n.IsRead, true)
        .SetProperty(n => n.ReadAt, now));

    return Results.Ok(new { markedRead = count });
}).WithName("NotificationMarkAllRead");

// FR-013: Devices list (modular API surface — minimal read endpoint for external systems).
app.MapGet("/api/devices", async (UkuuHrDbContext db, int? orgId) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var devices = await db.AttendanceDevices
        .Where(d => d.OrganizationId == oid && d.IsActive)
        .Select(d => new
        {
            d.Id,
            d.Name,
            vendor = d.Vendor.ToString(),
            mode = d.Mode.ToString(),
            d.IpAddress,
            d.Port,
            d.Location,
            d.LastSuccessfulSyncAt,
            d.TotalEventsSynced,
            d.AutoSyncEnabled,
            d.SyncIntervalMinutes
        })
        .ToListAsync();
    return Results.Ok(new { total = devices.Count, devices });
}).WithName("DevicesList");

// ───── POST /api/organization/seed — create a default organization ─────
app.MapPost("/api/organization/seed", async (UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var existing = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (existing != null)
        return Results.Redirect("/devices?saved=1&name=org_exists");

    var org = new Organization
    {
        Name = "My Organization",
        Country = "Zambia",
        Currency = "ZMW",
        Industry = "Human Resources",
        OwnerUserId = "system",
        CreatedAt = DateTime.UtcNow
    };
    db.Organizations.Add(org);
    await db.SaveChangesAsync();
    logger.LogInformation("Default organization created: {Id}", org.Id);
    return Results.Redirect("/devices?saved=1&name=org_created");
}).WithName("OrganizationSeed"); // P1/H-7: CSRF re-enabled

// ───── POST /api/devices/sync/{id} — sync a single device (works without Blazor circuit) ─────
app.MapPost("/api/devices/sync/{id:int}", async (
    int id,
    DeviceSyncOrchestrator orchestrator,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    logger.LogInformation("Device sync requested for device {Id}", id);
    var result = await orchestrator.SyncDeviceAsync(org.Id, id);
    if (result.Success)
        return Results.Redirect($"/devices?synced=1&name={Uri.EscapeDataString(result.EventsFetched.ToString())}");
    else
        return Results.Redirect($"/devices?synced=0&error={Uri.EscapeDataString(SanitizeRedirectMessage(result.ErrorMessage))}");
}).WithName("DeviceSync");

// ───── POST /api/devices/sync-all — sync all active devices ─────
app.MapPost("/api/devices/sync-all", async (
    DeviceSyncOrchestrator orchestrator,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    logger.LogInformation("Sync-all requested for org {OrgId}", org.Id);
    var results = await orchestrator.SyncAllDevicesAsync(org.Id);
    var ok = results.Count(r => r.Success);
    var fail = results.Count - ok;
    return Results.Redirect($"/devices?synced=1&name={ok}succeeded_{fail}failed");
}).WithName("DeviceSyncAll");

// ───── POST /api/devices/delete/{id} — delete a device ─────
app.MapPost("/api/devices/delete/{id:int}", async (
    int id,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id);
    if (device == null) return Results.NotFound(new { error = "Device not found." });

    var name = device.Name;
    db.AttendanceDevices.Remove(device);
    await db.SaveChangesAsync();
    logger.LogInformation("Device {Id} ({Name}) deleted", id, name);
    return Results.Redirect($"/devices?deleted=1&name={Uri.EscapeDataString(name)}");
}).WithName("DeviceDelete");

// ═══════════════════════════════════════════════════════════════════════════
// API KEY MANAGEMENT ENDPOINTS
// ═══════════════════════════════════════════════════════════════════════════

// ───── GET /api/api-keys — list all API keys for the org ─────
app.MapGet("/api/api-keys", async (HttpContext ctx, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var keys = await db.ApiKeys
        .Where(k => k.OrganizationId == org.Id)
        .OrderByDescending(k => k.CreatedAt)
        .Select(k => new
        {
            k.Id,
            k.Name,
            k.KeyPrefix,
            k.Scopes,
            k.RateLimitPerMinute,
            k.CreatedAt,
            k.ExpiresAt,
            k.LastUsedAt,
            k.LastUsedIp,
            k.TotalRequestCount,
            k.RevokedAt,
            k.RevokedByUserId,
            k.RevocationReason,
            IsActive = k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow),
            StatusDisplay = k.RevokedAt != null ? "Revoked" :
                           (k.ExpiresAt != null && k.ExpiresAt <= DateTime.UtcNow) ? "Expired" : "Active"
        })
        .ToListAsync();

    return Results.Ok(keys);
});

// ───── POST /api/api-keys/create — create a new API key ─────
app.MapPost("/api/api-keys/create", async (HttpContext ctx, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    // Get the current user's ID for audit trail
    var userId = ctx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";

    CreateApiKeyRequest? body;
    try { body = await ctx.Request.ReadFromJsonAsync<CreateApiKeyRequest>(); }
    catch (Exception) { return Results.BadRequest(new { error = "Invalid request body." }); }
    if (body == null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "Name is required." });
    if (body.Name.Length > 100)
        return Results.BadRequest(new { error = "Name must be 100 characters or less." });

    // Validate scopes
    var validScopes = new HashSet<string>(Enum.GetNames<ApiKeyScope>());
    var requestedScopes = (body.Scopes ?? new List<string> { "FullAccess" })
        .Where(s => validScopes.Contains(s)).ToList();
    if (requestedScopes.Count == 0)
        return Results.BadRequest(new { error = "At least one valid scope is required." });
    // If FullAccess is included, it supersedes all other scopes
    if (requestedScopes.Contains("FullAccess"))
        requestedScopes = new List<string> { "FullAccess" };

    // Generate a cryptographically secure API key: ukuu_<32-char-hex>
    var rawBytes = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(rawBytes);
    var rawKey = "ukuu_" + Convert.ToHexString(rawBytes).ToLowerInvariant();

    // Compute SHA-256 hash for storage
    var keyHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    // Extract prefix (first 8 chars after "ukuu_")
    var keyPrefix = rawKey[..13]; // "ukuu_" + 8 hex chars

    // Validate expiry
    DateTime? expiresAt = null;
    if (body.ExpiresInDays.HasValue && body.ExpiresInDays.Value > 0)
        expiresAt = DateTime.UtcNow.AddDays(body.ExpiresInDays.Value);

    var record = new ApiKeyRecord
    {
        OrganizationId = org.Id,
        CreatedByUserId = userId,
        Name = body.Name.Trim(),
        KeyHash = keyHash,
        KeyPrefix = keyPrefix,
        Scopes = string.Join(",", requestedScopes),
        RateLimitPerMinute = body.RateLimitPerMinute > 0 ? body.RateLimitPerMinute : 60,
        ExpiresAt = expiresAt,
    };

    db.ApiKeys.Add(record);
    await db.SaveChangesAsync();

    logger.LogInformation("API key '{Name}' created for org {OrgId} by user {UserId}", body.Name, org.Id, userId);

    // Return the raw key ONLY at creation time — it will never be shown again
    return Results.Ok(new
    {
        record.Id,
        record.Name,
        Key = rawKey,  // ⚠️ SHOWN ONLY ONCE — never stored or returned again
        record.KeyPrefix,
        record.Scopes,
        record.RateLimitPerMinute,
        record.CreatedAt,
        record.ExpiresAt,
        message = "Save this key securely. It will not be shown again."
    });
});

// ───── POST /api/api-keys/revoke/{id} — revoke an API key ─────
app.MapPost("/api/api-keys/revoke/{id:int}", async (
    int id, HttpContext ctx, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.OrganizationId == org.Id);
    if (key == null) return Results.NotFound(new { error = "API key not found." });
    if (key.RevokedAt != null) return Results.BadRequest(new { error = "API key is already revoked." });

    var userId = ctx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";

    RevokeApiKeyRequest? body;
    try { body = await ctx.Request.ReadFromJsonAsync<RevokeApiKeyRequest>(); }
    catch (Exception) { body = null; }

    key.RevokedAt = DateTime.UtcNow;
    key.RevokedByUserId = userId;
    key.RevocationReason = body?.Reason ?? "No reason provided";

    await db.SaveChangesAsync();
    logger.LogInformation("API key '{Name}' (id={Id}) revoked by user {UserId}: {Reason}", key.Name, id, userId, key.RevocationReason);

    return Results.Ok(new { key.Id, key.Name, key.RevokedAt, key.RevocationReason });
});

// ───── POST /api/api-keys/rotate/{id} — revoke old key and create a new one with same settings ─────
app.MapPost("/api/api-keys/rotate/{id:int}", async (
    int id, HttpContext ctx, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var oldKey = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.OrganizationId == org.Id);
    if (oldKey == null) return Results.NotFound(new { error = "API key not found." });

    var userId = ctx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";

    // Revoke the old key
    oldKey.RevokedAt = DateTime.UtcNow;
    oldKey.RevokedByUserId = userId;
    oldKey.RevocationReason = "Rotated — replaced by a new key";

    // Create a new key with the same settings
    var rawBytes = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(rawBytes);
    var rawKey = "ukuu_" + Convert.ToHexString(rawBytes).ToLowerInvariant();

    var keyHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    var newRecord = new ApiKeyRecord
    {
        OrganizationId = org.Id,
        CreatedByUserId = userId,
        Name = oldKey.Name,
        KeyHash = keyHash,
        KeyPrefix = rawKey[..13],
        Scopes = oldKey.Scopes,
        RateLimitPerMinute = oldKey.RateLimitPerMinute,
        ExpiresAt = oldKey.ExpiresAt,
    };

    db.ApiKeys.Add(newRecord);
    await db.SaveChangesAsync();

    logger.LogInformation("API key '{Name}' (id={OldId}) rotated to new key (id={NewId}) by user {UserId}",
        oldKey.Name, id, newRecord.Id, userId);

    return Results.Ok(new
    {
        OldKeyId = oldKey.Id,
        NewKeyId = newRecord.Id,
        newRecord.Name,
        Key = rawKey,
        newRecord.KeyPrefix,
        newRecord.Scopes,
        newRecord.RateLimitPerMinute,
        newRecord.ExpiresAt,
        message = "Old key revoked. Save the new key securely — it will not be shown again."
    });
});

// ───── DELETE /api/api-keys/{id} — permanently delete an API key ─────
app.MapDelete("/api/api-keys/{id:int}", async (
    int id, HttpContext ctx, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.OrganizationId == org.Id);
    if (key == null) return Results.NotFound(new { error = "API key not found." });

    var name = key.Name;
    db.ApiKeys.Remove(key);
    await db.SaveChangesAsync();

    logger.LogInformation("API key '{Name}' (id={Id}) permanently deleted", name, id);
    return Results.Ok(new { deleted = true, id, name });
});

// ───── POST /api/devices/save — create or update a device (traditional form POST) ─────
app.MapPost("/api/devices/save", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var idStr = form["Id"].ToString();
    var isEdit = int.TryParse(idStr, out var deviceId) && deviceId > 0;

    AttendanceDevice? device;
    if (isEdit)
    {
        device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == org.Id);
        if (device == null) return Results.NotFound(new { error = "Device not found." });
    }
    else
    {
        device = new AttendanceDevice
        {
            OrganizationId = org.Id,
            CreatedAt = DateTime.UtcNow,
            CreatedByEmail = "admin@ukuuhr.demo"
        };
    }

    device.Name = form["Name"].ToString();
    Enum.TryParse<DeviceVendor>(form["Vendor"].ToString(), out var vendor);
    device.Vendor = vendor;
    Enum.TryParse<DeviceIntegrationMode>(form["Mode"].ToString(), out var mode);
    device.Mode = mode;
    device.IpAddress = form["IpAddress"].ToString();
    device.Port = int.TryParse(form["Port"], out var port) ? port : (form["UseHttps"] == "true" || form["UseHttps"] == "on" ? 443 : 80);
    device.UseHttps = form["UseHttps"] == "true" || form["UseHttps"] == "on";
    device.Username = form["Username"].ToString();
    device.Password = form["Password"].ToString();
    // P2/H-5: Encrypt device password before storing in database
    try
    {
        var encSvc = ctx.RequestServices.GetRequiredService<AesEncryptionService>();
        device.PasswordEncrypted = encSvc.Encrypt(device.Password);
    }
    catch { /* encryption not available in dev — store plaintext */ }
    device.DeviceSerial = form["DeviceSerial"].ToString();
    device.Location = form["Location"].ToString();
    device.AutoSyncEnabled = form["AutoSyncEnabled"] == "true" || form["AutoSyncEnabled"] == "on";
    device.SyncIntervalMinutes = int.TryParse(form["SyncIntervalMinutes"], out var interval) ? interval : 5;
    if (!isEdit) device.IsActive = true; // preserve existing IsActive when editing
    device.UpdatedAt = DateTime.UtcNow;

    // CSV file path stored in ConnectionJson
    var csvPath = form["CsvFilePath"].ToString();
    if (device.Mode == DeviceIntegrationMode.CsvFile && !string.IsNullOrEmpty(csvPath))
    {
        device.ConnectionJson = $"{{\"filePath\":\"{csvPath}\"}}";
    }

    try
    {
        if (isEdit)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Device {Id} updated", deviceId);
        }
        else
        {
            db.AttendanceDevices.Add(device);
            await db.SaveChangesAsync();
            logger.LogInformation("Device {Id} created", device.Id);
        }
        return Results.Redirect($"/devices?saved=1&name={Uri.EscapeDataString(device.Name)}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save device");
        return Results.BadRequest(new { error = "Failed to save device. Please try again." }); // P2/M-4
    }
}).WithName("DeviceSave"); // P1/H-7: CSRF re-enabled

// ───── HikVision ISAPI Integration Endpoints ─────

// GET /api/hikvision/discover — SSDP device discovery on local network
app.MapGet("/api/hikvision/discover", async (ILogger<Program> logger) =>
{
    try
    {
        var devices = await UkuuHr.Services.Hikvision.HikvisionIsapiClient.DiscoverDevicesAsync(timeoutMs: 5000);
        return Results.Ok(devices.Select(d => new { d.IpAddress, d.Port, d.Username }));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "SSDP discovery failed");
        return Results.Ok(Array.Empty<object>());
    }
}).WithName("HikvisionDiscover");

// GET /api/hikvision/{id}/info — Get device info from ISAPI
app.MapGet("/api/hikvision/{id:int}/info", async (int id, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var info = await client.GetDeviceInfoAsync();
        var caps = await client.GetCapabilitiesAsync();
        return Results.Ok(new { info, caps });
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionDeviceInfo");

// GET /api/hikvision/{id}/health — Get device health status
app.MapGet("/api/hikvision/{id:int}/health", async (int id, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var health = await client.GetHealthAsync();
        return Results.Ok(health);
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionHealth");

// GET /api/hikvision/{id}/doors — Get door status
app.MapGet("/api/hikvision/{id:int}/doors", async (int id, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var doors = await client.GetDoorStatusAsync();
        return Results.Ok(doors);
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionDoors");

// POST /api/hikvision/{id}/unlock/{doorId} — Remotely unlock a door
app.MapPost("/api/hikvision/{id:int}/unlock/{doorId:int}", async (int id, int doorId, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var success = await client.UnlockDoorAsync(doorId);
        return Results.Ok(new { success, doorId });
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionUnlockDoor");

// POST /api/hikvision/{id}/sync-persons — Sync all employees to the device
app.MapPost("/api/hikvision/{id:int}/sync-persons", async (int id, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var employees = await db.Employees
            .Where(e => e.OrganizationId == org.Id && e.Status != EmploymentStatus.Inactive)
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.Department })
            .ToListAsync();

        var persons = employees.Select(e => (e.EmployeeCode ?? e.Id.ToString(), e.FullName, e.Department)).ToList();
        var results = await client.BatchSyncPersonsAsync(persons);
        var successCount = results.Count(r => r.Success);
        var failCount = results.Count(r => !r.Success);

        return Results.Ok(new { total = persons.Count, success = successCount, failed = failCount, results });
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionSyncPersons");

// ───── POST /api/devices/sync-persons/{id} — push portal employees to a Hikvision device (form POST, redirects) ─────
app.MapPost("/api/devices/sync-persons/{id:int}", async (
    int id,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.Redirect("/devices?pushed=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage("No organization found.")));

    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.Redirect("/devices?pushed=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage("Hikvision device not found.")));

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig
        {
            IpAddress = device.IpAddress ?? "",
            Port = device.Port ?? (device.UseHttps ? 443 : 80),
            UseHttps = device.UseHttps,
            Username = device.Username ?? "admin",
            Password = device.Password ?? ""
        },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var employees = await db.Employees
            .Where(e => e.OrganizationId == org.Id && e.Status != EmploymentStatus.Inactive)
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.Department })
            .ToListAsync();

        if (employees.Count == 0)
            return Results.Redirect("/devices?pushed=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage("No active employees to push.")));

        var persons = employees.Select(e => (e.EmployeeCode ?? e.Id.ToString(), e.FullName, e.Department)).ToList();
        var results = await client.BatchSyncPersonsAsync(persons);
        var successCount = results.Count(r => r.Success);
        var failCount = results.Count(r => !r.Success);

        logger.LogInformation("Pushed {Total} employees to device {DeviceName}: {Success} ok, {Failed} failed",
            persons.Count, device.Name, successCount, failCount);

        return Results.Redirect($"/devices?pushed=1&name={Uri.EscapeDataString(device.Name)}&total={persons.Count}&ok={successCount}&fail={failCount}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Employee push to device {DeviceName} failed", device.Name);
        return Results.Redirect("/devices?pushed=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage(ex.Message)));
    }
}).WithName("DeviceSyncPersonsForm").RequireAuthorization("HrOrAdmin"); // P1/H-7: CSRF re-enabled

// POST /api/hikvision/{id}/sync-time — Sync device time with server
app.MapPost("/api/hikvision/{id:int}/sync-time", async (int id, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var success = await client.SyncTimeAsync();
        return Results.Ok(new { success });
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionSyncTime");

// POST /api/hikvision/{id}/reboot — Reboot the device remotely
app.MapPost("/api/hikvision/{id:int}/reboot", async (int id, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });
    var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == org.Id && d.Vendor == DeviceVendor.Hikvision);
    if (device == null) return Results.NotFound(new { error = "Hikvision device not found." });

    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig { IpAddress = device.IpAddress ?? "", Port = device.Port ?? (device.UseHttps ? 443 : 80), UseHttps = device.UseHttps, Username = device.Username ?? "admin", Password = !string.IsNullOrEmpty(device.PasswordEncrypted) ? new AesEncryptionService().Decrypt(device.PasswordEncrypted) : device.Password ?? "" },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient> ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    try
    {
        var success = await client.RebootAsync();
        return Results.Ok(new { success });
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Device communication error. Please try again." }, statusCode: 502); // P2/M-4
    }
}).WithName("HikvisionReboot");

// ───── FR-010: Attendance report download endpoints ─────
// These endpoints allow Blazor pages to download CSV/Excel reports via a simple redirect,
// avoiding the need to write files to a server-side path.
// The /search variants accept all AttendanceSearchFilter params for filtered exports (FR-009).

app.MapGet("/api/reports/attendance/csv", async (
    ReportExportService reportSvc,
    UkuuHrDbContext db,
    int? orgId,
    string? from,
    string? to) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    DateTime? fromDate = DateTime.TryParse(from, out var fd) ? fd : null;
    DateTime? toDate = DateTime.TryParse(to, out var td) ? td : null;
    var report = await reportSvc.GenerateAsync(oid, ReportPeriod.Custom, fromDate, toDate);
    var bytes = reportSvc.ExportCsv(report);
    return Results.File(bytes, "text/csv", $"attendance-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
}).WithName("DownloadAttendanceCsv");

app.MapGet("/api/reports/attendance/xlsx", async (
    ReportExportService reportSvc,
    UkuuHrDbContext db,
    int? orgId,
    string? from,
    string? to) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    DateTime? fromDate = DateTime.TryParse(from, out var fd) ? fd : null;
    DateTime? toDate = DateTime.TryParse(to, out var td) ? td : null;
    var report = await reportSvc.GenerateAsync(oid, ReportPeriod.Custom, fromDate, toDate);
    var bytes = reportSvc.ExportXlsx(report);
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"attendance-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
}).WithName("DownloadAttendanceXlsx");

// FR-009: Filtered search-export endpoints — pass all AttendanceSearchFilter params
app.MapGet("/api/reports/attendance/csv/search", async (
    ReportExportService reportSvc,
    UkuuHrDbContext db,
    int? orgId,
    int? employeeId,
    string? department,
    string? branch,
    int? shiftId,
    string? status,
    string? search,
    string? from,
    string? to) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var filter = new AttendanceSearchFilter
    {
        EmployeeId = employeeId,
        Department = department,
        Branch = branch,
        ShiftId = shiftId,
        Status = Enum.TryParse<AttendanceStatus>(status, out var s) ? s : null,
        Search = search,
        FromDate = DateTime.TryParse(from, out var fd) ? fd : null,
        ToDate = DateTime.TryParse(to, out var td) ? td : null,
        Page = 1,
        PageSize = 100000
    };
    var report = await reportSvc.GenerateFromFilterAsync(oid, filter);
    var bytes = reportSvc.ExportCsv(report);
    return Results.File(bytes, "text/csv", $"attendance-search-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
}).WithName("DownloadAttendanceSearchCsv");

app.MapGet("/api/reports/attendance/xlsx/search", async (
    ReportExportService reportSvc,
    UkuuHrDbContext db,
    int? orgId,
    int? employeeId,
    string? department,
    string? branch,
    int? shiftId,
    string? status,
    string? search,
    string? from,
    string? to) =>
{
    var oid = await db.ResolveOrgIdAsync(orgId); // multi-tenant: principal org > orgId (anon only) > first org
    var filter = new AttendanceSearchFilter
    {
        EmployeeId = employeeId,
        Department = department,
        Branch = branch,
        ShiftId = shiftId,
        Status = Enum.TryParse<AttendanceStatus>(status, out var s) ? s : null,
        Search = search,
        FromDate = DateTime.TryParse(from, out var fd) ? fd : null,
        ToDate = DateTime.TryParse(to, out var td) ? td : null,
        Page = 1,
        PageSize = 100000
    };
    var report = await reportSvc.GenerateFromFilterAsync(oid, filter);
    var bytes = reportSvc.ExportXlsx(report);
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"attendance-search-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
}).WithName("DownloadAttendanceSearchXlsx");

// FR-013: System metrics endpoint (for monitoring dashboards / NFR — 99.9% availability).
app.MapGet("/api/system/metrics", (UkuuHrDbContext db) =>
{
    return Results.Ok(new
    {
        status = "ok",
        uptime_seconds = (DateTime.UtcNow - startTime).TotalSeconds,
        timestamp = DateTime.UtcNow,
        modules_active = new[]
        {
            "employees", "attendance", "shifts", "leave",
            "payroll", "reporting", "devices", "auto-sync"
        }
    });
}).WithName("SystemMetrics");

// ───── OpenAPI: expose /openapi/v1.json + Scalar UI at /api-docs ─────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("UkuuHR API")
               .WithTheme(ScalarTheme.Purple);
    });
}

// Phase 24: Security endpoints — work without Blazor circuit

// POST /api/security/policies — save security policy toggles (traditional form POST)
app.MapPost("/api/security/policies", async (HttpContext ctx, UkuuHrDbContext db, ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    logger.LogInformation("Security policies POST received with {Count} keys", form.Keys.Count);

    // For now, policies are in-memory (not persisted to DB) — the form submission
    // just redirects back with a success message. In a future iteration, we'd
    // store these in a SecurityPolicies table.
    return Results.Redirect("/security?saved=1");
}).WithName("SecurityPoliciesSave");

// GET /api/security/audit-log.csv — export audit log as CSV
app.MapGet("/api/security/audit-log.csv", async (UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    var logs = org != null
        ? await db.AuditLogs.Where(a => a.OrganizationId == org.Id).OrderByDescending(a => a.Timestamp).Take(500).ToListAsync()
        : new List<UkuuHr.Models.AuditLog>();

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Action,PerformedBy,TargetUser,Details,Timestamp");
    foreach (var a in logs)
    {
        sb.AppendLine(string.Join(",",
            $"\"{a.ActionDisplay}\"",
            $"\"{a.PerformedByEmail ?? ""}\"",
            $"\"{a.TargetUserEmail ?? ""}\"",
            $"\"{a.Details ?? ""}\"",
            a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));
    }
    var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    return Results.File(bytes, "text/csv", "audit-log.csv");
}).WithName("SecurityAuditLogExport");

// Phase 27: Document upload + export endpoints

// POST /api/documents/upload — handle file upload (traditional form POST)
app.MapPost("/api/documents/upload", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var name = form["name"].ToString().Trim();
    var employeeIdStr = form["employeeId"].ToString();
    var categoryStr = form["category"].ToString();
    var uploadedBy = form["uploadedBy"].ToString().Trim();
    var description = form["description"].ToString().Trim();
    var file = form.Files.FirstOrDefault();

    logger.LogInformation("Document upload: name={Name}, category={Category}, hasFile={HasFile}", name, categoryStr, file != null);

    if (string.IsNullOrWhiteSpace(name) || file == null || file.Length == 0)
        return Results.Redirect("/documents/upload?error=1");

    if (file.Length > 10 * 1024 * 1024)
        return Results.Redirect("/documents/upload?error=1");

    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.Redirect("/documents/upload?error=1");

    // Parse category
    Enum.TryParse<DocumentCategory>(categoryStr, out var category);

    // Parse employee ID
    int? employeeId = null;
    if (int.TryParse(employeeIdStr, out var eid) && eid > 0)
        employeeId = eid;

    // Determine document type from extension
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    // P2/M-5: Explicit extension allowlist — reject dangerous file types
    var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt"
    };
    if (!allowedExtensions.Contains(ext))
    {
        logger.LogWarning("Document upload rejected — disallowed extension: {Ext}", ext);
        return Results.Redirect("/documents/upload?error=1&msg=File type not allowed");
    }
    var docType = ext switch
    {
        ".pdf" => DocumentType.Pdf,
        ".jpg" or ".jpeg" or ".png" or ".gif" => DocumentType.Image,
        ".doc" or ".docx" => DocumentType.Word,
        ".xls" or ".xlsx" => DocumentType.Excel,
        _ => DocumentType.Other
    };

    // Save file to wwwroot/uploads/ (in production, this would be S3/cloud storage)
    var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
    Directory.CreateDirectory(uploadsDir);
    var fileName = $"{Guid.NewGuid():N}{ext}";
    var filePath = Path.Combine(uploadsDir, fileName);
    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    var doc = new EmployeeDocument
    {
        OrganizationId = org.Id,
        EmployeeId = employeeId ?? 0,
        Name = name,
        Type = docType,
        Category = category,
        Folder = DocumentFolder.All,
        SizeBytes = file.Length,
        DownloadUrl = $"/uploads/{fileName}",
        UploadedBy = string.IsNullOrWhiteSpace(uploadedBy) ? "HR" : "HR",
        UploadedByName = string.IsNullOrWhiteSpace(uploadedBy) ? "Admin" : uploadedBy,
        Description = string.IsNullOrWhiteSpace(description) ? null : description,
        UploadedAt = DateTime.UtcNow
    };

    db.EmployeeDocuments.Add(doc);
    await db.SaveChangesAsync();

    logger.LogInformation("Document saved: {Name} ({Size} bytes), ID={Id}", name, file.Length, doc.Id);
    return Results.Redirect("/documents/upload?saved=1");
}).WithName("DocumentUpload"); // P1/H-7: CSRF re-enabled

// GET /api/documents/export.csv — export document list as CSV
app.MapGet("/api/documents/export.csv", async (UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    var docs = org != null
        ? await db.EmployeeDocuments.Where(d => d.OrganizationId == org.Id).OrderByDescending(d => d.UploadedAt).ToListAsync()
        : new List<EmployeeDocument>();

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Name,Category,Type,Size,UploadedBy,Date");
    foreach (var d in docs)
    {
        sb.AppendLine(string.Join(",",
            $"\"{d.Name}\"",
            d.Category.ToString(),
            d.Type.ToString(),
            d.FormattedSize,
            $"\"{d.UploadedByName ?? ""}\"",
            d.UploadedAt.ToString("yyyy-MM-dd")));
    }
    var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    return Results.File(bytes, "text/csv", "documents.csv");
}).WithName("DocumentExport");

// Phase 28: POST /api/employees/save — save employee via traditional form POST (no Blazor circuit)
app.MapPost("/api/employees/save", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    EmployeeService svc,
    LicenseService licenses,
    IHostEnvironment env,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var isEdit = form["isEdit"] == "true";
    var empIdStr = form["employeeId"].ToString();

    logger.LogInformation("Employee save POST: isEdit={IsEdit}, empId={EmpId}", isEdit, empIdStr);

    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found" });

    // Subscription enforcement: block NEW hires beyond the plan limit (Production only).
    if (!isEdit)
    {
        var (allowed, reason) = await licenses.CanAddEmployeeAsync(org.Id, env);
        if (!allowed)
            return Results.Json(new { error = reason }, statusCode: 402); // 402 Payment Required
    }

    Employee? emp;
    if (isEdit && int.TryParse(empIdStr, out var eid) && eid > 0)
    {
        emp = await svc.GetAsync(org.Id, eid);
        if (emp == null) return Results.NotFound(new { error = "Employee not found" });
    }
    else
    {
        emp = new Employee { OrganizationId = org.Id, CreatedAt = DateTime.UtcNow };
    }

    // Map form fields to model
    emp.Title = form["Title"].ToString();
    emp.FirstName = form["FirstName"].ToString();
    emp.MiddleNames = form["MiddleNames"].ToString();
    emp.Surname = form["Surname"].ToString();
    emp.Nationality = form["Nationality"].ToString();
    emp.Country = form["Country"].ToString();
    emp.City = form["City"].ToString();
    emp.Gender = form["Gender"].ToString();
    emp.MaritalStatus = form["MaritalStatus"].ToString();
    emp.Email = form["Email"].ToString();
    emp.Phone = form["Phone"].ToString();
    emp.StreetAddress = form["StreetAddress"].ToString();
    emp.PostalCode = form["PostalCode"].ToString();
    emp.NationalIdentityNumber = form["NationalIdentityNumber"].ToString();
    emp.PassportNumber = form["PassportNumber"].ToString();
    emp.DateOfBirth = DateTime.TryParse(form["DateOfBirth"], out var dob) ? dob : null;

    // Emergency contact — Phase 29
    emp.EmergencyContactName = form["EmergencyContactName"].ToString();
    emp.EmergencyContactRelationship = form["EmergencyContactRelationship"].ToString();
    emp.EmergencyContactPhone = form["EmergencyContactPhone"].ToString();
    emp.EmergencyContactEmail = form["EmergencyContactEmail"].ToString();

    // Employment
    emp.EmployeeCode = form["EmployeeCode"].ToString();
    emp.PayrollId = form["PayrollId"].ToString();
    emp.JobTitle = form["JobTitle"].ToString();
    emp.Department = form["Department"].ToString();
    emp.EmploymentType = form["EmploymentType"].ToString();
    emp.ContractType = form["ContractType"].ToString();
    emp.ContractEndDate = DateTime.TryParse(form["ContractEndDate"], out var ced) ? ced : null;
    emp.WorkHoursPerWeek = double.TryParse(form["WorkHoursPerWeek"], out var whpw) ? whpw : 40;
    emp.JoiningDate = DateTime.TryParse(form["JoiningDate"], out var jd) ? jd : null;
    emp.ReportingManagerName = form["ReportingManagerName"].ToString();
    // Phase 29: Round out employment fields so nothing in the wizard is silently dropped
    emp.ReportingManagerTitle = form["ReportingManagerTitle"].ToString();
    emp.PlaceOfWork = form["PlaceOfWork"].ToString();
    emp.TerminationNoticePeriod = form["TerminationNoticePeriod"].ToString();
    if (int.TryParse(form["HolidayEntitlementDays"], out var hed)) emp.HolidayEntitlementDays = hed;
    if (int.TryParse(form["ProbationaryPeriodMonths"], out var ppm)) emp.ProbationaryPeriodMonths = ppm;
    emp.JobDescription = form["JobDescription"].ToString();

    // P2 fix: only override Status when the form actually posts a valid value.
    // Previously every UI edit silently reset the status to Active (the wizard
    // never sends this field) — deactivating an employee via edit was impossible.
    if (Enum.TryParse<EmploymentStatus>(form["Status"].ToString(), out var postedStatus) && postedStatus != default)
        emp.Status = postedStatus;
    // ShiftId from the wizard's "Assign to Shift" dropdown (previously dead — the
    // field was posted but never read, so the assignment silently never happened).
    var shiftIdStr = form["ShiftId"].ToString();
    var shiftId = int.TryParse(shiftIdStr, out var sid) && sid > 0 ? sid : (int?)null;

    emp.BasicSalary = double.TryParse(form["BasicSalary"], out var bs) ? bs : 0;
    emp.HourlyRate = double.TryParse(form["HourlyRate"], out var hr) ? hr : null;
    // Company branch/location assignment (Branch entity; distinct from the bank branch below).
    if (int.TryParse(form["BranchId"], out var bid) && bid > 0)
    {
        var branchExists = await db.Branches.AnyAsync(b => b.Id == bid && b.OrganizationId == org.Id && b.IsActive);
        emp.BranchId = branchExists ? bid : null;
    }
    else emp.BranchId = null;
    emp.BankName = form["BankName"].ToString();
    emp.Branch = form["Branch"].ToString();
    emp.AccountNumber = form["AccountNumber"].ToString();
    emp.MobileMoney = form["MobileMoney"].ToString();
    emp.BeneficiaryName = form["BeneficiaryName"].ToString();
    emp.Currency = string.IsNullOrEmpty(form["Currency"].ToString()) ? "ZMW" : form["Currency"].ToString();
    emp.SwiftCode = form["SwiftCode"].ToString();
    emp.IbanNumber = form["IbanNumber"].ToString();
    emp.RoutingNumbers = form["RoutingNumbers"].ToString();

    emp.Tpin = form["Tpin"].ToString();
    emp.NapsaNumber = form["NapsaNumber"].ToString();
    emp.HealthInsuranceNumber = form["HealthInsuranceNumber"].ToString();

    emp.UpdatedAt = DateTime.UtcNow;

    try
    {
        if (isEdit)
        {
            await svc.UpdateAsync(emp);
            logger.LogInformation("Employee updated: {Name}", emp.FullName);
        }
        else
        {
            await svc.CreateAsync(emp);
            logger.LogInformation("Employee created: {Name}", emp.FullName);
        }

        // Wire the optional shift assignment from the wizard (create + edit paths).
        if (shiftId.HasValue)
        {
            var shiftExists = await db.Shifts.AnyAsync(s => s.Id == shiftId.Value && s.OrganizationId == org.Id);
            if (shiftExists)
            {
                var existingAssignment = await db.EmployeeShiftAssignments
                    .FirstOrDefaultAsync(a => a.OrganizationId == org.Id && a.EmployeeId == emp.Id && a.IsPrimary);
                if (existingAssignment == null)
                {
                    db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
                    {
                        OrganizationId = org.Id,
                        EmployeeId = emp.Id,
                        ShiftId = shiftId.Value,
                        EffectiveFrom = DateTime.Today,
                        IsPrimary = true,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
                else if (existingAssignment.ShiftId != shiftId.Value)
                {
                    existingAssignment.ShiftId = shiftId.Value;
                    existingAssignment.EffectiveFrom = DateTime.Today;
                    existingAssignment.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
            }
        }

        return Results.Ok(new { success = true, name = emp.FullName, id = emp.Id });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save employee");
        return Results.BadRequest(new { error = "Failed to save device. Please try again." }); // P2/M-4
    }
}).WithName("EmployeeSave"); // P1/H-7: CSRF re-enabled

// ───── GET /api/employees/export — CSV export of the employee directory ─────
app.MapGet("/api/employees/export", async (
    EmployeeService svc,
    UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });
    var bytes = await svc.ExportCsvAsync(org.Id);
    return Results.File(bytes, "text/csv", $"ukuuhr-employees-{DateTime.UtcNow:yyyyMMdd}.csv");
}).WithName("EmployeesExportCsv");

// ───── GET /api/employees/export/xlsx — Excel export of the employee directory ─────
app.MapGet("/api/employees/export/xlsx", async (
    EmployeeService svc,
    UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });
    var bytes = await svc.ExportXlsxAsync(org.Id);
    return Results.File(bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"ukuuhr-employees-{DateTime.UtcNow:yyyyMMdd}.xlsx");
}).WithName("EmployeesExportXlsx");

// ───── POST /api/employees/import — bulk CSV import (multipart form file) ─────
app.MapPost("/api/employees/import", async (
    HttpContext ctx,
    EmployeeService svc,
    UkuuHrDbContext db,
    AuditService audit,
    LicenseService licenses,
    IHostEnvironment env,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    // Subscription enforcement: block imports that would exceed the plan limit (Production only).
    var (importAllowed, importReason) = await licenses.CanAddEmployeeAsync(org.Id, env);
    if (!importAllowed)
        return Results.Redirect("/employees?imported=0&skipped=0&error=" + Uri.EscapeDataString(importReason!));

    try
    {
        var file = ctx.Request.Form.Files.FirstOrDefault(f => f.Name == "file" || f.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        if (file == null || file.Length == 0)
            return Results.Redirect("/employees?imported=0&skipped=0&error=" + Uri.EscapeDataString("No CSV file received (field name must be 'file')."));

        using var stream = file.OpenReadStream();
        var result = await svc.ImportCsvAsync(org.Id, stream);
        logger.LogInformation("Employee CSV import: {Imported} imported, {Skipped} skipped", result.Imported, result.Skipped);

        await audit.LogAsync(org.Id, AuditAction.EmployeeImported,
            ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            $"Imported {result.Imported} employees from CSV ({result.Skipped} skipped).");

        var errorNote = result.Errors.Count > 0
            ? "&error=" + Uri.EscapeDataString(SanitizeRedirectMessage(string.Join("; ", result.Errors.Take(3))))
            : "";
        return Results.Redirect($"/employees?imported={result.Imported}&skipped={result.Skipped}{errorNote}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Employee CSV import failed");
        return Results.Redirect("/employees?imported=0&skipped=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage(ex.Message)));
    }
}).DisableAntiforgery().WithName("EmployeesImportCsv");

// ───── POST /api/employees/{id}/status — activate / deactivate / terminate (form POST) ─────
app.MapPost("/api/employees/{id:int}/status", async (
    HttpContext ctx,
    int id,
    EmployeeService svc,
    UkuuHrDbContext db,
    AuditService audit) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var form = await ctx.Request.ReadFormAsync();
    var statusStr = form["Status"].ToString();
    if (!Enum.TryParse<EmploymentStatus>(statusStr, out var newStatus))
        return Results.Redirect($"/employees?error={Uri.EscapeDataString("Invalid status value.")}");

    var emp = await db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == org.Id && e.Id == id);
    if (emp == null) return Results.NotFound(new { error = "Employee not found." });

    var previous = emp.Status;
    emp.Status = newStatus;
    emp.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    await audit.LogAsync(org.Id, AuditAction.EmployeeStatusChanged,
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
        $"Employee '{emp.FullName}' status: {previous} → {newStatus}.",
        targetUserEmail: emp.Email,
        previousValue: previous.ToString(),
        newValue: newStatus.ToString());

    return Results.Redirect($"/employees?status={Uri.EscapeDataString(newStatus.ToString())}&name={Uri.EscapeDataString(emp.FullName)}");
}).DisableAntiforgery().WithName("EmployeeSetStatus");

// ───── POST /api/employees/delete/{id} — permanently delete an employee (form POST) ─────
app.MapPost("/api/employees/delete/{id:int}", async (
    HttpContext ctx,
    int id,
    EmployeeService svc,
    UkuuHrDbContext db,
    AuditService audit) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var emp = await db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == org.Id && e.Id == id);
    if (emp == null) return Results.NotFound(new { error = "Employee not found." });

    // Hard-delete cascade: remove every dependent row first (SQLite/Postgres enforce
    // FK constraints) so the employee record itself can be removed cleanly.
    var attendances = db.Attendances.Where(a => a.OrganizationId == org.Id && a.EmployeeId == id);
    var overtimes = db.OvertimeRecords.Where(o => o.OrganizationId == org.Id && o.EmployeeId == id);
    var leaveRequests = db.LeaveRequests.Where(l => l.OrganizationId == org.Id && l.EmployeeId == id);
    var leaveBalances = db.LeaveBalances.Where(b => b.OrganizationId == org.Id && b.EmployeeId == id);
    var shiftAssignments = db.EmployeeShiftAssignments.Where(a => a.OrganizationId == org.Id && a.EmployeeId == id);
    var payrollRuns = db.PayrollRuns.Where(p => p.OrganizationId == org.Id && p.EmployeeId == id);
    var documents = db.EmployeeDocuments.Where(d => d.OrganizationId == org.Id && d.EmployeeId == id);
    var expenses = db.ExpenseRequests.Where(x => x.OrganizationId == org.Id && x.EmployeeId == id);
    await attendances.ExecuteDeleteAsync();
    await overtimes.ExecuteDeleteAsync();
    await leaveRequests.ExecuteDeleteAsync();
    await leaveBalances.ExecuteDeleteAsync();
    await shiftAssignments.ExecuteDeleteAsync();
    await payrollRuns.ExecuteDeleteAsync();
    await documents.ExecuteDeleteAsync();
    await expenses.ExecuteDeleteAsync();

    // Raw clock events keep their payload as an audit trail — just unlink the employee.
    await db.UnifiedClockEvents.Where(c => c.OrganizationId == org.Id && c.EmployeeId == id)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.EmployeeId, (int?)null));
    await db.HikvisionClockEvents.Where(c => c.OrganizationId == org.Id && c.EmployeeId == id)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.EmployeeId, (int?)null));

    // Unlink any user account pointing at this employee before removing it.
    var linkedAccounts = await db.UserAccounts.Where(u => u.EmployeeId == id).ToListAsync();
    foreach (var account in linkedAccounts) account.EmployeeId = null;

    var deleted = await svc.DeleteAsync(org.Id, id);
    if (!deleted) return Results.NotFound(new { error = "Employee not found." });

    await audit.LogAsync(org.Id, AuditAction.EmployeeDeleted,
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
        $"Employee '{emp.FullName}' ({emp.EmployeeCode}) permanently deleted.",
        targetUserEmail: emp.Email);

    return Results.Redirect($"/employees?deleted={Uri.EscapeDataString(emp.FullName)}");
}).DisableAntiforgery().WithName("EmployeeDelete");

// ───── GET /api/overtime — JSON list of overtime records (REST resource) ─────
// Query params: status (Pending|Approved|Rejected|AutoApproved), from, to, employeeId, limit.
app.MapGet("/api/overtime", async (
    OvertimeService svc,
    UkuuHrDbContext db,
    string? status,
    DateTime? from,
    DateTime? to,
    int? employeeId,
    int? limit) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var q = db.OvertimeRecords.Where(o => o.OrganizationId == org.Id);
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OvertimeStatus>(status, true, out var st))
        q = q.Where(o => o.Status == st);
    if (from.HasValue) q = q.Where(o => o.Date >= from.Value.Date);
    if (to.HasValue) q = q.Where(o => o.Date < to.Value.Date.AddDays(1));
    if (employeeId.HasValue) q = q.Where(o => o.EmployeeId == employeeId.Value);

    q = q.OrderByDescending(o => o.Date).ThenByDescending(o => o.CreatedAt);
    if (limit is > 0) q = q.Take(limit.Value);

    var records = await q.ToListAsync();
    return Results.Ok(records.Select(o => new
    {
        o.Id,
        o.EmployeeId,
        o.EmployeeName,
        date = o.Date.ToString("yyyy-MM-dd"),
        startTime = o.StartTime.ToString("HH:mm"),
        endTime = o.EndTime.ToString("HH:mm"),
        o.Hours,
        rateType = o.RateType.ToString(),
        o.RateMultiplier,
        o.HourlyRate,
        pay = Math.Round(o.Pay, 2),
        source = o.Source.ToString(),
        status = o.Status.ToString(),
        o.Reason,
        approvedByEmail = o.ApprovedByEmail,
        approvedAt = o.ApprovedAt
    }));
}).WithName("OvertimeList");

// ───── POST /api/attendance/{id}/edit — manual attendance correction (form POST, audited) ─────
app.MapPost("/api/attendance/{id:int}/edit", async (
    HttpContext ctx,
    int id,
    UkuuHrDbContext db,
    AuditService audit,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var record = await db.Attendances.FirstOrDefaultAsync(a => a.OrganizationId == org.Id && a.Id == id);
    if (record == null) return Results.NotFound(new { error = "Attendance record not found." });

    var form = await ctx.Request.ReadFormAsync();
    var actor = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "system";

    // Snapshot the pre-correction state for the audit trail.
    var before = $"in={record.CheckIn:HH:mm}, out={record.CheckOut:HH:mm}, status={record.Status}, break={record.BreakMinutes}m";

    DateTime? ParseLocalTime(string key)
    {
        var raw = form[key].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, out var t) ? t : null;
    }

    var newCheckIn = ParseLocalTime("CheckIn");
    var newCheckOut = ParseLocalTime("CheckOut");
    var newStatus = Enum.TryParse<AttendanceStatus>(form["Status"].ToString(), out var st) ? st : record.Status;
    var newBreak = int.TryParse(form["BreakMinutes"], out var bm) ? bm : record.BreakMinutes;

    if (newCheckIn == null && newCheckOut == null && newStatus == record.Status && newBreak == record.BreakMinutes)
        return Results.Redirect("/attendance?corrected=0");

    if (newCheckIn.HasValue && newCheckOut.HasValue && newCheckOut < newCheckIn)
        return Results.Redirect("/attendance?corrected=0&error=" + Uri.EscapeDataString("Check-out cannot be before check-in."));

    record.CheckIn = newCheckIn ?? record.CheckIn;
    record.CheckOut = newCheckOut ?? record.CheckOut;
    record.Status = newStatus;
    record.BreakMinutes = newBreak;
    var notes = form["Notes"].ToString();
    if (!string.IsNullOrWhiteSpace(notes)) record.Notes = notes;
    record.Source = AttendanceSource.Manual;
    record.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    var after = $"in={record.CheckIn:HH:mm}, out={record.CheckOut:HH:mm}, status={record.Status}, break={record.BreakMinutes}m";
    await audit.LogAsync(org.Id, AuditAction.AttendanceCorrected, actor,
        $"Corrected attendance for {record.EmployeeName} on {record.Date:yyyy-MM-dd}.",
        targetUserEmail: null,
        previousValue: before,
        newValue: after);

    logger.LogInformation("Attendance {Id} corrected by {Actor}: {Before} → {After}", id, actor, before, after);
    return Results.Redirect($"/attendance?corrected=1&name={Uri.EscapeDataString(record.EmployeeName)}");
}).DisableAntiforgery().WithName("AttendanceCorrect");

// ───── POST /api/shifts/save — create or update a shift from a plain HTML form ─────
app.MapPost("/api/shifts/save", async (
    HttpContext ctx,
    ShiftService svc,
    UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var form = await ctx.Request.ReadFormAsync();
    var actor = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

    static int? ParseMinutes(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h is >= 0 and < 24 && m is >= 0 and < 60)
            return h * 60 + m;
        return null;
    }

    var startMinutes = ParseMinutes(form["StartTime"].ToString());
    var endMinutes = ParseMinutes(form["EndTime"].ToString());
    if (startMinutes == null || endMinutes == null)
        return Results.Redirect("/shifts?saved=0&error=" + Uri.EscapeDataString("Start and End time must be in HH:mm format."));

    var shift = new Shift
    {
        Name = form["Name"].ToString(),
        Description = string.IsNullOrWhiteSpace(form["Description"].ToString()) ? null : form["Description"].ToString(),
        Kind = Enum.TryParse<ShiftKind>(form["Kind"].ToString(), out var kind) ? kind : ShiftKind.Fixed,
        Color = form["Color"].ToString(),
        StartMinutes = startMinutes.Value,
        EndMinutes = endMinutes.Value,
        BreakMinutes = int.TryParse(form["BreakMinutes"], out var brk) ? brk : 60,
        RotationCycleDays = int.TryParse(form["RotationCycleDays"], out var rcd) ? rcd : 7,
        RotationSlots = int.TryParse(form["RotationSlots"], out var rs) ? rs : 2
    };

    // Day-of-week checkboxes: fields named "day0".."day6" (Mon..Sun).
    var mask = 0;
    for (var i = 0; i < 7; i++)
        if (form[$"day{i}"].ToString() is "true" or "on" or "1")
            mask |= 1 << i;
    shift.DaysOfWeekMask = mask == 0 ? 0b0011111 : mask;

    var idStr = form["Id"].ToString();
    if (int.TryParse(idStr, out var editId) && editId > 0)
    {
        shift.Id = editId;
        try
        {
            await svc.UpdateShiftAsync(org.Id, shift, actor);
            return Results.Redirect("/shifts?saved=1");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Redirect("/shifts?saved=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage(ex.Message)));
        }
    }

    try
    {
        await svc.CreateShiftAsync(org.Id, shift, actor);
        return Results.Redirect("/shifts?saved=1");
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        return Results.Redirect("/shifts?saved=0&error=" + Uri.EscapeDataString(SanitizeRedirectMessage(ex.Message)));
    }
}).DisableAntiforgery().WithName("ShiftSaveForm");

// ───── GET /api/admin/backup — full JSON data snapshot (download) ─────
// Admin-only disaster-recovery export: every business table in one JSON file.
// Restore path: re-import via the APIs / database tooling. Sensitive employee
// fields remain AES-encrypted exactly as stored.
app.MapGet("/api/admin/backup", async (
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    try
    {
        var snapshot = new
        {
            format = "ukuuhr-backup",
            version = 1,
            generatedAt = DateTime.UtcNow,
            organization = await db.Organizations.AsNoTracking().ToListAsync(),
            employees = await db.Employees.AsNoTracking().ToListAsync(),
            attendances = await db.Attendances.AsNoTracking().ToListAsync(),
            shifts = await db.Shifts.AsNoTracking().ToListAsync(),
            shiftAssignments = await db.EmployeeShiftAssignments.AsNoTracking().ToListAsync(),
            tolerance = await db.AttendanceTolerances.AsNoTracking().ToListAsync(),
            leaveTypes = await db.LeaveTypes.AsNoTracking().ToListAsync(),
            leaveRequests = await db.LeaveRequests.AsNoTracking().ToListAsync(),
            leaveBalances = await db.LeaveBalances.AsNoTracking().ToListAsync(),
            holidays = await db.LeaveHolidays.AsNoTracking().ToListAsync(),
            overtimeRecords = await db.OvertimeRecords.AsNoTracking().ToListAsync(),
            attendanceDevices = await db.AttendanceDevices.AsNoTracking()
                .Select(d => new { d.Id, d.OrganizationId, d.Name, d.Vendor, d.Mode, d.IpAddress, d.Port, d.UseHttps, d.Username, d.Location, d.DeviceSerial, d.IsActive, d.AutoSyncEnabled, d.SyncIntervalMinutes, d.CreatedAt })
                .ToListAsync(),
            payrollRuns = await db.PayrollRuns.AsNoTracking().ToListAsync(),
            auditLogs = await db.AuditLogs.AsNoTracking().ToListAsync(),
            notifications = await db.Set<NotificationRecord>().AsNoTracking().ToListAsync()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
        });

        return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json",
            $"ukuuhr-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Backup generation failed");
        return Results.Json(new { error = "Backup generation failed. Please try again." }, statusCode: 500);
    }
}).RequireAuthorization("AdminOnly").WithName("AdminBackup");

// ───── POST /api/payroll/{id}/email-payslip — email the payslip to the employee (Resend) ─────
app.MapPost("/api/payroll/{id:int}/email-payslip", async (
    HttpContext ctx,
    int id,
    UkuuHrDbContext db,
    EmailService email,
    ILogger<Program> logger) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var run = await db.PayrollRuns.FirstOrDefaultAsync(p => p.OrganizationId == org.Id && p.Id == id);
    if (run == null) return Results.NotFound(new { error = "Payroll run not found." });

    var employee = await db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == org.Id && e.Id == run.EmployeeId);
    var to = employee?.Email;
    if (string.IsNullOrWhiteSpace(to))
        return Results.Redirect($"/payroll/{id}/payslip?emailed=0&error=" + Uri.EscapeDataString("Employee has no email address on file."));

    if (!email.Enabled)
        return Results.Redirect($"/payroll/{id}/payslip?emailed=0&error=" + Uri.EscapeDataString("Email is not configured — set RESEND_API_KEY on the server."));

    var period = $"{System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(run.Month)} {run.Year}";
    var html = EmailService.WrapHtml($"Payslip — {period}",
        $@"<p>Hi {run.EmployeeName},</p>
<p>Your payslip for <b>{period}</b> is ready.</p>
<table style=""border-collapse:collapse;width:100%;margin:12px 0;"">
  <tr><td style=""padding:6px 0;color:#6b6580;"">Gross</td><td style=""text-align:right;font-weight:700;"">{run.Currency} {run.Gross:N2}</td></tr>
  <tr><td style=""padding:6px 0;color:#6b6580;"">PAYE</td><td style=""text-align:right;"">− {run.Currency} {run.Paye:N2}</td></tr>
  <tr><td style=""padding:6px 0;color:#6b6580;"">NAPSA</td><td style=""text-align:right;"">− {run.Currency} {run.Napsa:N2}</td></tr>
  <tr><td style=""padding:6px 0;color:#6b6580;"">NHIMA</td><td style=""text-align:right;"">− {run.Currency} {run.Nhima:N2}</td></tr>
  <tr style=""border-top:2px solid #E8E4F0;""><td style=""padding:8px 0;font-weight:800;"">Net Pay</td><td style=""text-align:right;font-weight:800;color:#25163F;"">{run.Currency} {run.Net:N2}</td></tr>
</table>
<p style=""color:#6b6580;font-size:12px;"">Full breakdown available in your Ukuu HR account.</p>");

    var sent = await email.SendAsync(to, $"Your Ukuu HR payslip — {period}", html);
    if (sent)
    {
        run.PayslipDelivery = PayslipDeliveryStatus.Sent;
        run.SentToEmail = to;
        run.SentAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        logger.LogInformation("Payslip {Id} emailed to {To}", id, to);
    }

    return Results.Redirect($"/payroll/{id}/payslip?emailed={(sent ? 1 : 0)}");
}).DisableAntiforgery().WithName("PayslipEmail");


// ───── POST /api/overtime/{id}/edit — edit overtime record (form POST) ─────
app.MapPost("/api/overtime/{id:int}/edit", async (
    HttpContext ctx,
    OvertimeService svc,
    UkuuHrDbContext db,
    int id,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    logger.LogInformation("Overtime edit POST received for id={Id}", id);

    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var existing = await svc.GetAsync(org.Id, id);
    if (existing == null) return Results.NotFound(new { error = "Overtime record not found." });

    // Parse hours
    if (!double.TryParse(form["Hours"].ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var hours) || hours <= 0)
        return Results.BadRequest(new { error = "Please provide valid overtime hours." });

    // Parse times
    if (!DateTime.TryParse(form["StartTime"].ToString(), out var startTime))
        startTime = existing.StartTime;
    if (!DateTime.TryParse(form["EndTime"].ToString(), out var endTime))
        endTime = existing.EndTime;

    // Parse rate type
    Enum.TryParse<OvertimeRateType>(form["RateType"].ToString(), out var rateType);
    if (rateType == default) rateType = existing.RateType;

    var reason = form["Reason"].ToString();
    if (string.IsNullOrWhiteSpace(reason)) reason = existing.Reason;

    var updated = await svc.UpdateAsync(org.Id, id, hours, startTime, endTime, rateType, reason);
    if (updated == null) return Results.NotFound(new { error = "Failed to update overtime record." });

    logger.LogInformation("Overtime {Id} updated: {Hours}h {RateType}", id, hours, rateType);
    return Results.Redirect("/overtime?tab=all&updated=1");
}).WithName("OvertimeEdit"); // P1/H-7: CSRF re-enabled

// ───── POST /api/overtime/add — manual overtime entry (works without Blazor circuit) ─────
app.MapPost("/api/overtime/add", async (
    HttpContext ctx,
    OvertimeService svc,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    logger.LogInformation("Overtime add POST received");

    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    // Parse employee
    if (!int.TryParse(form["EmployeeId"].ToString(), out var empId) || empId <= 0)
        return Results.BadRequest(new { error = "Please select an employee." });

    var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == empId && e.OrganizationId == org.Id);
    if (emp == null)
        return Results.BadRequest(new { error = "Employee not found in this organization." });

    // Parse date
    if (!DateTime.TryParse(form["Date"].ToString(), out var date))
        return Results.BadRequest(new { error = "Please provide a valid date." });

    // Parse times — combine with the date
    if (!TimeSpan.TryParse(form["StartTime"].ToString(), out var startTs))
        return Results.BadRequest(new { error = "Please provide a valid start time." });
    if (!TimeSpan.TryParse(form["EndTime"].ToString(), out var endTs))
        return Results.BadRequest(new { error = "Please provide a valid end time." });

    var startTime = date.Date.Add(startTs);
    var endTime = date.Date.Add(endTs);

    // Handle overnight overtime (end time is next day)
    if (endTime < startTime)
        endTime = endTime.AddDays(1);

    if (endTime <= startTime)
        return Results.BadRequest(new { error = "End time must be after start time." });

    // Parse rate type
    Enum.TryParse<OvertimeRateType>(form["RateType"].ToString(), out var rateType);
    if (rateType == default) rateType = OvertimeRateType.Standard;

    var record = new OvertimeRecord
    {
        OrganizationId = org.Id,
        EmployeeId = emp.Id,
        EmployeeName = emp.FullName,
        Date = date.Date,
        StartTime = startTime,
        EndTime = endTime,
        RateType = rateType,
        HourlyRate = emp.EffectiveHourlyRate,
        Source = OvertimeSource.Manual,
        Status = OvertimeStatus.Pending,
        Reason = string.IsNullOrWhiteSpace(form["Reason"].ToString()) ? null : form["Reason"].ToString(),
        CreatedAt = DateTime.UtcNow
    };

    try
    {
        await svc.CreateManualAsync(record);
        logger.LogInformation("Overtime record created: {Name} on {Date} ({Hours}h)", record.EmployeeName, record.Date.ToString("yyyy-MM-dd"), record.Hours);
        // Redirect back to the overtime list with a success flag (traditional form POST flow)
        return Results.Redirect("/overtime?added=1&tab=pending");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to create overtime record");
        return Results.BadRequest(new { error = "Failed to save device. Please try again." }); // P2/M-4
    }
}).WithName("OvertimeAdd"); // P1/H-7: CSRF re-enabled

// ───── GET /api/overtime/export — export overtime records as Excel-compatible CSV ─────
// Matches the "Worked Hrs" format from the CassTech biometric system export
app.MapGet("/api/overtime/export", async (
    OvertimeService svc,
    UkuuHrDbContext db,
    string? tab) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var allRecords = await svc.GetAllAsync(org.Id);

    // Filter by tab
    IEnumerable<OvertimeRecord> filtered = tab switch
    {
        "pending" => allRecords.Where(o => o.Status == OvertimeStatus.Pending),
        "approved" => allRecords.Where(o => o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved),
        _ => allRecords
    };

    // Load employee details
    var empIds = filtered.Select(o => o.EmployeeId).Distinct().ToList();
    var employees = await db.Employees.Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);

    // Build CSV in the "Worked Hrs" format matching the CassTech export
    var sb = new StringBuilder();
    sb.AppendLine("Ukuu HR");
    sb.AppendLine("Worked Hrs (Overtime)");
    sb.AppendLine($"Export Time: {DateTime.Now:yyyy-MM-dd HH:mm}");
    sb.AppendLine();
    sb.AppendLine("First Name,Last Name,ID,Department,Date,Weekday,OT Hours,Workday Overtime,Weekend Overtime,Holiday Overtime,Rate Type,OT Pay,Source,Status");

    foreach (var ot in filtered.OrderByDescending(o => o.Date))
    {
        var emp = employees.TryGetValue(ot.EmployeeId, out var e) ? e : null;
        var firstName = emp?.FirstName ?? ot.EmployeeName?.Split(' ').FirstOrDefault() ?? "";
        var lastName = emp?.Surname ?? (ot.EmployeeName?.Contains(' ') == true ? ot.EmployeeName.Split(' ', 2)[1] : "");
        var empCode = emp?.EmployeeCode ?? "";
        var dept = emp?.Department ?? "";

        var isWorkday = ot.RateType == OvertimeRateType.Standard || ot.RateType == OvertimeRateType.DoubleTime;
        var isWeekend = ot.RateType == OvertimeRateType.RestDay;
        var isHoliday = ot.RateType == OvertimeRateType.PublicHoliday;

        var workdayOT = isWorkday ? $"{ot.Hours:F1}h" : "0.0h";
        var weekendOT = isWeekend ? $"{ot.Hours:F1}h" : "0.0h";
        var holidayOT = isHoliday ? $"{ot.Hours:F1}h" : "0.0h";

        var source = ot.Source == OvertimeSource.AutoCalculated ? "Auto" : ot.Source == OvertimeSource.Hikvision ? "Hikvision" : "Manual";

        sb.AppendLine($"{EscapeCsvField(firstName)},{EscapeCsvField(lastName)},{EscapeCsvField(empCode)},{EscapeCsvField(dept)},{ot.Date:yyyy-MM-dd},{ot.Date:dddd},{ot.Hours:F1}h,{workdayOT},{weekendOT},{holidayOT},{ot.RateTypeDisplay},ZMW {ot.Pay:F0},{source},{ot.StatusDisplay}");
    }

    var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    var filename = $"overtime-{tab ?? "all"}-{DateTime.Now:yyyyMMdd-HHmm}.csv";
    return Results.File(bytes, "text/csv; charset=utf-8", filename);
}).WithName("OvertimeExport");

static string EscapeCsvField(string? value)
{
    if (string.IsNullOrEmpty(value)) return "";
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
}

// ───── GET /api/time-cards/export — export time cards as CSV ─────
app.MapGet("/api/time-cards/export", async (
    TimeCardService svc,
    UkuuHrDbContext db,
    string? tab,
    string? date) =>
{
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var parsedDate = DateTime.TryParse(date, out var d) ? d : DateTime.Today;
    var mode = tab ?? "daily";

    var sb = new StringBuilder();
    sb.AppendLine("Ukuu HR");
    sb.AppendLine($"Time Cards ({mode})");
    sb.AppendLine($"Export Time: {DateTime.Now:yyyy-MM-dd HH:mm}");
    sb.AppendLine();

    if (mode == "daily")
    {
        var rows = await svc.GetDailyAsync(org.Id, parsedDate);
        sb.AppendLine("First Name,Last Name,Employee ID,Clock In,Clock Out,Worked Hrs,Late Hrs,Overtime Hrs,Status,Shift");
        foreach (var r in rows)
        {
            sb.AppendLine($"{EscapeCsvField(r.FirstName)},{EscapeCsvField(r.LastName)},{EscapeCsvField(r.EmployeeCode)},{r.CheckInLabel},{r.CheckOutLabel},{r.WorkedHours:F1}h,{r.LateHours:F2}h,{r.OvertimeHours:F1}h,{r.Status},{EscapeCsvField(r.ShiftName)}");
        }
    }
    else if (mode == "weekly")
    {
        var rows = await svc.GetWeeklyAsync(org.Id, parsedDate);
        sb.AppendLine("First Name,Last Name,Employee ID,Department,Mon,Tue,Wed,Thu,Fri,Sat,Sun,Late Hrs,Total Worked,OT Workday,OT Weekend,OT Holiday");
        foreach (var r in rows)
        {
            var days = string.Join(",", Enumerable.Range(0, 7).Select(i => r.DailyHours[i] > 0 ? $"{r.DailyHours[i]:F1}h" : r.DailyStatus[i] ?? "—"));
            sb.AppendLine($"{EscapeCsvField(r.FirstName)},{EscapeCsvField(r.LastName)},{EscapeCsvField(r.EmployeeCode)},{EscapeCsvField(r.Department)},{days},{r.LateHours:F2}h,{r.TotalWorkedHours:F1}h,{r.OvertimeWorkday:F1}h,{r.OvertimeWeekend:F1}h,{r.OvertimeHoliday:F1}h");
        }
    }
    else
    {
        var rows = await svc.GetMonthlyAsync(org.Id, parsedDate);
        var daysInMonth = DateTime.DaysInMonth(parsedDate.Year, parsedDate.Month);
        var headers = Enumerable.Range(1, daysInMonth).Select(d => d.ToString("00"));
        sb.AppendLine($"First Name,Last Name,Employee ID,{string.Join(",", headers)},Total Worked,OT Workday,OT Weekend,OT Holiday,Late Hrs");
        foreach (var r in rows)
        {
            var days = string.Join(",", Enumerable.Range(0, daysInMonth).Select(i => r.DailyHours[i] > 0 ? $"{r.DailyHours[i]:F1}h" : r.DailyStatus[i] ?? "—"));
            sb.AppendLine($"{EscapeCsvField(r.FirstName)},{EscapeCsvField(r.LastName)},{EscapeCsvField(r.EmployeeCode)},{days},{r.TotalWorkedHours:F1}h,{r.OvertimeWorkday:F1}h,{r.OvertimeWeekend:F1}h,{r.OvertimeHoliday:F1}h,{r.LateHours:F2}h");
        }
    }

    var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    var filename = $"time-card-{mode}-{parsedDate:yyyyMMdd}.csv";
    return Results.File(bytes, "text/csv; charset=utf-8", filename);
}).WithName("TimeCardExport");

// ───────────── Coupon Management API (Super Admin) ─────────────

// POST /api/super-admin/coupons/create — create a new coupon (requires super_admin role)
app.MapPost("/api/super-admin/coupons/create", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    if (!ctx.User.IsInRole("super_admin"))
        return Results.Json(new { error = "Unauthorized. Super Admin role required." }, statusCode: 403);

    var form = await ctx.Request.ReadFormAsync();
    var code = form["Code"].ToString().Trim().ToUpperInvariant();
    var description = form["Description"].ToString().Trim();
    var discountPercent = int.TryParse(form["DiscountPercent"], out var dp) ? dp : 100;
    var applicablePlan = form["ApplicablePlan"].ToString().Trim();
    var maxUses = int.TryParse(form["MaxUses"], out var mu) ? mu : 1;
    var expiresAtStr = form["ExpiresAtStr"].ToString().Trim();

    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect("/super-admin?error=Code is required");
    if (!DateTime.TryParse(expiresAtStr, out var expiresAt))
        return Results.Redirect("/super-admin?error=Valid expiry date is required");

    var existing = await db.CouponCodes.FirstOrDefaultAsync(c => c.Code == code);
    if (existing != null)
        return Results.Redirect("/super-admin?error=Coupon code already exists");

    var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "admin@ukuuhr.demo";

    db.CouponCodes.Add(new CouponCode
    {
        Code = code,
        Description = description,
        DiscountPercent = Math.Clamp(discountPercent, 0, 100),
        ApplicablePlan = string.IsNullOrWhiteSpace(applicablePlan) ? "Annual" : applicablePlan,
        MaxUses = Math.Max(0, maxUses),
        UsedCount = 0,
        ExpiresAt = expiresAt,
        IsActive = true,
        CreatedByEmail = email,
        CreatedAt = DateTime.UtcNow
    });
    await db.SaveChangesAsync();

    logger.LogInformation("Coupon created: {Code} by {Email}", code, email);
    return Results.Redirect("/super-admin?created=1");
}).WithName("CouponCreate"); // P1/H-7: CSRF re-enabled

// POST /api/super-admin/coupons/revoke — revoke a coupon
app.MapPost("/api/super-admin/coupons/revoke", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    if (!ctx.User.IsInRole("super_admin"))
        return Results.Json(new { error = "Unauthorized." }, statusCode: 403);

    var form = await ctx.Request.ReadFormAsync();
    var couponId = int.TryParse(form["CouponId"], out var id) ? id : 0;

    var coupon = await db.CouponCodes.FirstOrDefaultAsync(c => c.Id == couponId);
    if (coupon == null)
        return Results.Redirect("/super-admin?error=Coupon not found");

    coupon.IsActive = false;
    await db.SaveChangesAsync();

    logger.LogInformation("Coupon revoked: {Code} by {Email}", coupon.Code,
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown");
    return Results.Redirect("/super-admin?revoked=1");
}).WithName("CouponRevoke"); // P1/H-7: CSRF re-enabled

// POST /api/subscription/redeem-coupon — redeem a coupon (any authenticated user)
// Coupons now provision/extend a real LicenseCode (previously granted nothing).
app.MapPost("/api/subscription/redeem-coupon", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    LicenseService licenses,
    ILogger<Program> logger) =>
{
    if (!ctx.User.Identity?.IsAuthenticated == true)
        return Results.Json(new { error = "Unauthorized. Please sign in." }, statusCode: 401);

    var form = await ctx.Request.ReadFormAsync();
    var code = form["CouponCode"].ToString().Trim().ToUpperInvariant();

    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect("/billing?coupon=error&msg=" + Uri.EscapeDataString("Code is required"));

    var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown";
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null)
        return Results.Redirect("/billing?coupon=error&msg=" + Uri.EscapeDataString("No organization found"));

    var (success, message, _) = await licenses.RedeemCouponAsync(org.Id, code, email);
    logger.LogInformation("Coupon redemption attempt by {Email} for org {OrgId}: {Result}", email, org.Id, message);

    return Results.Redirect(success
        ? "/billing?coupon=success&msg=" + Uri.EscapeDataString(message)
        : "/billing?coupon=error&msg=" + Uri.EscapeDataString(message));
}).WithName("CouponRedeem"); // P1/H-7: CSRF re-enabled

// GET /api/subscription/status — current license posture (plan, limits, usage)
app.MapGet("/api/subscription/status", async (
    UkuuHrDbContext db,
    LicenseService licenses) =>
{
    var org = await db.ResolveOrgAsync();
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var status = await licenses.GetStatusAsync(org.Id);
    return Results.Ok(new
    {
        organization = org.Name,
        hasLicense = status.HasLicense,
        plan = status.PlanName,
        licenseCode = status.License?.Code,
        activatedAt = status.License?.ActivatedAt,
        expiresAt = status.License?.ExpiresAt,
        daysRemaining = status.DaysRemaining,
        employeeCount = status.EmployeeCount,
        employeeLimit = status.EmployeeLimit == int.MaxValue ? (int?)null : status.EmployeeLimit,
        overLimit = status.IsOverLimit,
        message = status.Message
    });
}).WithName("SubscriptionStatus");

// POST /api/subscription/activate — bind an issued license code to this org
app.MapPost("/api/subscription/activate", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    LicenseService licenses) =>
{
    var org = await db.ResolveOrgAsync();
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var form = await ctx.Request.ReadFormAsync();
    var code = form["LicenseCode"].ToString();
    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect("/billing?activate=error&msg=" + Uri.EscapeDataString("Enter a license code."));

    var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown";
    var (success, message) = await licenses.ActivateAsync(org.Id, code, email);

    return Results.Redirect(success
        ? "/billing?activate=success&msg=" + Uri.EscapeDataString(message)
        : "/billing?activate=error&msg=" + Uri.EscapeDataString(message));
}).DisableAntiforgery().WithName("LicenseActivate");

// ───── Branch / location management (Module 1.3 + Module 2.2) ─────

// GET /api/branches — list branches (active + deactivated, with employee counts)
app.MapGet("/api/branches", async (UkuuHrDbContext db) =>
{
    var org = await db.ResolveOrgAsync();
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var branches = await db.Branches
        .Where(b => b.OrganizationId == org.Id)
        .OrderBy(b => b.Name)
        .ToListAsync();
    var counts = await db.Employees
        .Where(e => e.OrganizationId == org.Id && e.BranchId != null)
        .GroupBy(e => e.BranchId!.Value)
        .Select(g => new { BranchId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.BranchId, x => x.Count);

    return Results.Ok(branches.Select(b => new
    {
        b.Id,
        b.Name,
        b.City,
        b.Address,
        b.ContactPhone,
        b.IsActive,
        employeeCount = counts.GetValueOrDefault(b.Id)
    }));
}).WithName("BranchList");

// POST /api/branches/save — create or update a branch (form POST)
app.MapPost("/api/branches/save", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    AuditService audit) =>
{
    var org = await db.ResolveOrgAsync();
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var form = await ctx.Request.ReadFormAsync();
    var name = form["Name"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.Redirect("/settings/branches?saved=0&error=" + Uri.EscapeDataString("Branch name is required."));

    var idStr = form["Id"].ToString();
    Branch? branch;
    if (int.TryParse(idStr, out var editId) && editId > 0)
    {
        branch = await db.Branches.FirstOrDefaultAsync(b => b.OrganizationId == org.Id && b.Id == editId);
        if (branch == null) return Results.NotFound(new { error = "Branch not found." });
    }
    else
    {
        branch = new Branch { OrganizationId = org.Id, CreatedAt = DateTime.UtcNow };
        db.Branches.Add(branch);
    }

    branch.Name = name;
    branch.City = form["City"].ToString();
    branch.Address = form["Address"].ToString();
    branch.ContactPhone = form["ContactPhone"].ToString();
    branch.IsActive = form["IsActive"] != "false";
    branch.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    await audit.LogAsync(org.Id,
        branch.Id == editId ? AuditAction.ProfileUpdated : AuditAction.UserCreated,
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
        $"Branch '{branch.Name}' saved.");

    return Results.Redirect("/settings/branches?saved=1");
}).DisableAntiforgery().WithName("BranchSave");

// POST /api/branches/delete/{id} — deactivate a branch (soft delete; employees unassigned)
app.MapPost("/api/branches/delete/{id:int}", async (
    HttpContext ctx,
    int id,
    UkuuHrDbContext db,
    AuditService audit) =>
{
    var org = await db.ResolveOrgAsync();
    if (org == null) return Results.NotFound(new { error = "No organization found." });

    var branch = await db.Branches.FirstOrDefaultAsync(b => b.OrganizationId == org.Id && b.Id == id);
    if (branch == null) return Results.NotFound(new { error = "Branch not found." });

    branch.IsActive = false;
    branch.UpdatedAt = DateTime.UtcNow;
    // Unassign employees so reports fall back to their city.
    await db.Employees
        .Where(e => e.OrganizationId == org.Id && e.BranchId == id)
        .ExecuteUpdateAsync(s => s.SetProperty(e => e.BranchId, (int?)null));

    await db.SaveChangesAsync();
    await audit.LogAsync(org.Id, AuditAction.ProfileUpdated,
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
        $"Branch '{branch.Name}' deactivated; assigned employees unassigned.");

    return Results.Redirect("/settings/branches?saved=1");
}).DisableAntiforgery().WithName("BranchDeactivate");

// ─────────────────────────────────────────────────────────────────────────────
// POST /api/attendance/import-from-device
// Live import of attendance events from a Hikvision terminal.
// Body: { ipAddress, port, useHttps, username, password, from?, to?, maxResults? }
// Does NOT require the device to be pre-registered in the AttendanceDevices table —
// credentials are passed in the request body so the user can import ad-hoc.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/attendance/import-from-device", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    AttendanceService attendanceSvc,
    ILogger<Program> logger) =>
{
    // ── 1. Parse + validate the request body ────────────────────────────────
    ImportFromDeviceRequest? body;
    try { body = await ctx.Request.ReadFromJsonAsync<ImportFromDeviceRequest>(); }
    catch (Exception) { return Results.BadRequest(new { error = "Invalid request body." }); } // P2/M-4
    if (body == null) return Results.BadRequest(new { error = "Empty request body." });

    if (string.IsNullOrWhiteSpace(body.IpAddress))
        return Results.BadRequest(new { error = "IP address is required." });
    if (string.IsNullOrWhiteSpace(body.Username))
        return Results.BadRequest(new { error = "Username is required." });
    if (string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest(new { error = "Password is required." });

    var port = body.Port ?? (body.UseHttps ? 443 : 80);
    var useHttps = body.UseHttps;
    var maxResults = body.MaxResults is > 0 and <= 5000 ? body.MaxResults.Value : 1000;
    var from = body.From ?? DateTime.UtcNow.AddDays(-7);
    var to = body.To ?? DateTime.UtcNow;

    // ── 2. Resolve the org (events get attached to the org's employees) ─────
    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found. Create an organization first." });

    logger.LogInformation("ImportFromDevice: connecting to {Scheme}://{Host}:{Port} as {User} (range {From} → {To}, max {Max})",
        useHttps ? "https" : "http", body.IpAddress, port, body.Username, from, to, maxResults);

    // ── 3. Construct a one-shot HikvisionIsapiClient with the provided creds ─
    using var client = new UkuuHr.Services.Hikvision.HikvisionIsapiClient(
        new UkuuHr.Services.Hikvision.HikvisionIsapiConfig
        {
            IpAddress = body.IpAddress.Trim(),
            Port = port,
            UseHttps = useHttps,
            Username = body.Username.Trim(),
            Password = body.Password,
            TimeoutSeconds = 30,
            MaxRetries = 1,
            RetryDelayMs = 500
        },
        logger as ILogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>
            ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UkuuHr.Services.Hikvision.HikvisionIsapiClient>());

    // ── 4. Step 1: probe device info (validates connection + auth) ───────────
    string deviceName = "Unknown", deviceModel = "Unknown", deviceSerial = "Unknown";
    try
    {
        var info = await client.GetDeviceInfoAsync();
        deviceName = info.DeviceName;
        deviceModel = info.Model;
        deviceSerial = info.SerialNumber;
        logger.LogInformation("ImportFromDevice: connected to {Name} ({Model}) serial={Serial}", deviceName, deviceModel, deviceSerial);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "ImportFromDevice: deviceInfo probe failed for {Host}", body.IpAddress);
        return Results.Json(new
        {
            error = $"Could not connect to the Hikvision device at {(useHttps ? "https" : "http")}://{body.IpAddress}:{port}. " +
                    $"Check the IP, port, HTTPS toggle, and that your machine is on the same network as the device. " +
                    $"Detail: {ex.Message}",
            stage = "connect"
        }, statusCode: 502);
    }

    // ── 4b. Step 1b: probe ISAPI Face Recognition service ────────────────────
    // This checks whether the device supports face recognition and whether the
    // face database is configured. The result is included in the import summary
    // so the user can see "ISAPI Face Recognition: Enabled" before events are
    // imported.
    object? faceRecognitionStatus = null;
    try
    {
        var faceStatus = await client.GetFaceRecognitionStatusAsync();
        if (faceStatus != null)
        {
            faceRecognitionStatus = new
            {
                serviceAvailable = faceStatus.ServiceAvailable,
                maxFaceTemplates = faceStatus.MaxFaceTemplates,
                faceContrastEnabled = faceStatus.FaceContrastEnabled,
                faceCaptureEnabled = faceStatus.FaceCaptureEnabled,
                faceDatabaseCount = faceStatus.FaceDatabaseCount
            };
            logger.LogInformation("ImportFromDevice: ISAPI Face Recognition service available (maxTemplates={Max}, faceDBs={Dbs})",
                faceStatus.MaxFaceTemplates, faceStatus.FaceDatabaseCount);
        }
        else
        {
            faceRecognitionStatus = new { serviceAvailable = false, note = "Face Recognition endpoint not found — device may not support it or ISAPI Face Recognition may be disabled in firmware." };
            logger.LogInformation("ImportFromDevice: ISAPI Face Recognition service not available on {Host}", body.IpAddress);
        }
    }
    catch (Exception ex)
    {
        faceRecognitionStatus = new { serviceAvailable = false, error = ex.Message };
        logger.LogWarning(ex, "ImportFromDevice: Face Recognition probe failed (non-fatal)");
    }

    // ── 5. Step 2: fetch attendance events ───────────────────────────────────
    List<UkuuHr.Services.Devices.NormalizedClockEvent> events;
    try
    {
        events = await client.FetchAttendanceEventsAsync(from, maxResults);
        logger.LogInformation("ImportFromDevice: fetched {Count} events", events.Count);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "ImportFromDevice: fetch failed for {Host}", body.IpAddress);
        return Results.Json(new
        {
            error = $"Connected to the device, but could not fetch attendance events. " +
                    $"The device may not support the AcsEvent/AuditLog endpoints, or the date range may be invalid. " +
                    $"Detail: {ex.Message}",
            stage = "fetch",
            device = new { name = deviceName, model = deviceModel, serial = deviceSerial }
        }, statusCode: 502);
    }

    // ── 6. Step 3: match events to employees by EmployeeCode + upsert attendance ─
    var employees = await db.Employees.Where(e => e.OrganizationId == org.Id).ToListAsync();
    var byCode = employees.Where(e => !string.IsNullOrEmpty(e.EmployeeCode))
                          .ToDictionary(e => e.EmployeeCode!, e => e, StringComparer.OrdinalIgnoreCase);

    int matched = 0, unmatched = 0, imported = 0, skippedDupe = 0;
    var unmatchedSamples = new List<string>();
    var errors = new List<string>();

    // Group events by (employeeCode, date) so we can take the earliest check-in and latest check-out per day.
    var grouped = events
        .Where(e => !string.IsNullOrEmpty(e.EmployeeCode))
        .GroupBy(e => (e.EmployeeCode, e.EventTime.Date))
        .ToList();

    foreach (var grp in grouped)
    {
        var (code, date) = grp.Key;
        if (!byCode.TryGetValue(code!, out var emp))
        {
            unmatched++;
            if (unmatchedSamples.Count < 5) unmatchedSamples.Add(code!);
            continue;
        }
        matched++;

        var dateKey = date.ToString("yyyy-MM-dd");
        var checkIns = grp.Where(g => g.EventType == UkuuHr.Models.ClockEventType.CheckIn).Select(g => g.EventTime).ToList();
        var checkOuts = grp.Where(g => g.EventType == UkuuHr.Models.ClockEventType.CheckOut).Select(g => g.EventTime).ToList();
        if (checkIns.Count == 0 && checkOuts.Count > 0) checkIns = grp.Select(g => g.EventTime).ToList(); // fall back: treat first event as check-in
        if (checkOuts.Count == 0 && checkIns.Count > 1) checkOuts = new List<DateTime> { checkIns.Last() };

        var checkIn = checkIns.Count > 0 ? checkIns.Min() : (DateTime?)null;
        var checkOut = checkOuts.Count > 0 ? checkOuts.Max() : (DateTime?)null;
        if (checkIn == null) { skippedDupe++; continue; }

        try
        {
            // Find or create the attendance record for this employee + date
            // Use AsNoTracking so EF Core doesn't pull existing rows into the change tracker
            // (which would cause "modified" instead of "added" for new dates).
            var existing = await db.Attendances
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.OrganizationId == org.Id && a.EmployeeId == emp.Id && a.DateKey == dateKey);

            if (existing == null)
            {
                var att = new UkuuHr.Models.Attendance
                {
                    OrganizationId = org.Id,
                    EmployeeId = emp.Id,
                    EmployeeName = emp.FullName,
                    DateKey = dateKey,
                    Date = date,
                    Status = UkuuHr.Models.AttendanceStatus.Present,
                    Source = UkuuHr.Models.AttendanceSource.Import,
                    BreakMinutes = 60,
                    CreatedAt = DateTime.UtcNow,
                    CheckIn = checkIn,
                    CheckOut = checkOut
                };
                db.Attendances.Add(att);
                imported++;
            }
            else
            {
                // Update the existing row — fetch it tracked so we can modify it
                var att = await db.Attendances.FirstAsync(a => a.Id == existing.Id);
                var changed = false;
                if (checkIn.HasValue && (!att.CheckIn.HasValue || checkIn < att.CheckIn)) { att.CheckIn = checkIn; changed = true; }
                if (checkOut.HasValue && (!att.CheckOut.HasValue || checkOut > att.CheckOut)) { att.CheckOut = checkOut; changed = true; }
                if (changed)
                {
                    att.Source = UkuuHr.Models.AttendanceSource.Import; // mark as device-imported
                    att.CreatedAt = DateTime.UtcNow;
                    imported++;
                }
                else skippedDupe++;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Employee {code} on {dateKey}: {ex.Message}");
        }
    }

    // ── 7. Persist + return the summary ──────────────────────────────────────
    try
    {
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "ImportFromDevice: SaveChanges failed");
        return Results.Json(new
        {
            error = $"Fetched {events.Count} events and matched {matched} employees, but failed to save to the database: {ex.Message}",
            stage = "save",
            eventsFetched = events.Count,
            employeesMatched = matched
        }, statusCode: 500);
    }

    return Results.Ok(new
    {
        success = true,
        device = new { name = deviceName, model = deviceModel, serial = deviceSerial },
        faceRecognition = faceRecognitionStatus,
        eventsFetched = events.Count,
        employeesMatched = matched,
        employeesUnmatched = unmatched,
        unmatchedSampleCodes = unmatchedSamples,
        recordsImported = imported,
        duplicatesSkipped = skippedDupe,
        errors = errors,
        dateRange = new { from = from, to = to }
    });
}).WithName("AttendanceImportFromDevice"); // P1/H-7: CSRF re-enabled

// ─────────────────────────────────────────────────────────────────────────────
// POST /api/attendance/save-imported
// Accepts pre-fetched attendance events from the browser (which fetched them
// directly from the Hikvision device) and saves them to the database.
// This endpoint is used when the browser can reach the device but the server
// cannot (e.g. device is on a LAN, server is in the cloud).
// Body: { events: [{ employeeNo, time, major, minor, eventType }], deviceInfo, faceRecognition }
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/attendance/save-imported", async (
    HttpContext ctx,
    UkuuHrDbContext db,
    ILogger<Program> logger) =>
{
    SaveImportedRequest? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SaveImportedRequest>(); }
    catch (Exception) { return Results.BadRequest(new { error = "Invalid request body." }); } // P2/M-4
    if (body == null || body.Events == null) return Results.BadRequest(new { error = "No events in request body." });

    var org = await db.ResolveOrgAsync(); // multi-tenant org resolution
    if (org == null) return Results.BadRequest(new { error = "No organization found." });

    var employees = await db.Employees.Where(e => e.OrganizationId == org.Id).ToListAsync();
    var byCode = employees.Where(e => !string.IsNullOrEmpty(e.EmployeeCode))
                          .ToDictionary(e => e.EmployeeCode!, e => e, StringComparer.OrdinalIgnoreCase);

    int matched = 0, unmatched = 0, imported = 0, skippedDupe = 0;
    var unmatchedSamples = new List<string>();

    // Group events by (employeeCode, date)
    var grouped = body.Events
        .Where(e => !string.IsNullOrEmpty(e.EmployeeNo))
        .GroupBy(e => { DateTime.TryParse(e.Time, out var d); return (e.EmployeeNo, d.Date); })
        .Where(g => g.Key.Date != DateTime.MinValue);

    foreach (var grp in grouped)
    {
        var (code, date) = grp.Key;
        if (!byCode.TryGetValue(code!, out var emp))
        {
            unmatched++;
            if (unmatchedSamples.Count < 5) unmatchedSamples.Add(code!);
            continue;
        }
        matched++;

        var dateKey = date.ToString("yyyy-MM-dd");
        var checkIns = grp.Where(g => g.EventType == "check_in").Select(g => DateTime.Parse(g.Time)).ToList();
        var checkOuts = grp.Where(g => g.EventType == "check_out").Select(g => DateTime.Parse(g.Time)).ToList();
        if (checkIns.Count == 0 && checkOuts.Count > 0) checkIns = grp.Select(g => DateTime.Parse(g.Time)).ToList();
        if (checkOuts.Count == 0 && checkIns.Count > 1) checkOuts = new List<DateTime> { checkIns.Last() };

        var checkIn = checkIns.Count > 0 ? checkIns.Min() : (DateTime?)null;
        var checkOut = checkOuts.Count > 0 ? checkOuts.Max() : (DateTime?)null;
        if (checkIn == null) { skippedDupe++; continue; }

        var existing = await db.Attendances.AsNoTracking()
            .FirstOrDefaultAsync(a => a.OrganizationId == org.Id && a.EmployeeId == emp.Id && a.DateKey == dateKey);

        if (existing == null)
        {
            db.Attendances.Add(new UkuuHr.Models.Attendance
            {
                OrganizationId = org.Id, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                DateKey = dateKey, Date = date, Status = UkuuHr.Models.AttendanceStatus.Present,
                Source = UkuuHr.Models.AttendanceSource.Import, BreakMinutes = 60,
                CreatedAt = DateTime.UtcNow, CheckIn = checkIn, CheckOut = checkOut
            });
            imported++;
        }
        else
        {
            var att = await db.Attendances.FirstAsync(a => a.Id == existing.Id);
            var changed = false;
            if (checkIn.HasValue && (!att.CheckIn.HasValue || checkIn < att.CheckIn)) { att.CheckIn = checkIn; changed = true; }
            if (checkOut.HasValue && (!att.CheckOut.HasValue || checkOut > att.CheckOut)) { att.CheckOut = checkOut; changed = true; }
            if (changed) { att.Source = UkuuHr.Models.AttendanceSource.Import; att.CreatedAt = DateTime.UtcNow; imported++; }
            else skippedDupe++;
        }
    }

    try { await db.SaveChangesAsync(); }
    catch (Exception ex)
    {
        logger.LogError(ex, "SaveImported: SaveChanges failed");
        return Results.Json(new { error = "Save failed. Please try again." }, statusCode: 500); // P2/M-4
    }

    return Results.Ok(new
    {
        success = true,
        device = body.DeviceInfo ?? new { },
        faceRecognition = body.FaceRecognition,
        eventsFetched = body.Events.Count,
        employeesMatched = matched,
        employeesUnmatched = unmatched,
        unmatchedSampleCodes = unmatchedSamples,
        recordsImported = imported,
        duplicatesSkipped = skippedDupe,
        errors = new List<string>()
    });
}).WithName("AttendanceSaveImported"); // P1/H-7: CSRF re-enabled

// Deployment: Map Blazor Hub at /_framework/blazor instead of the default /_blazor.
// This container's Caddy proxy routes /_framework/* to the C# app, but NOT /_blazor.
// Without this change, the Blazor SignalR circuit fails to connect.
app.MapBlazorHub("/_framework/blazor");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ───── Phase 5: FR-013 — Module info DTO for the modular API surface ─────
public sealed record ModuleInfo(string Key, string Name, bool Implemented, string? Endpoint);

// DTO for /api/attendance/import-from-device — live Hikvision attendance import
public sealed record ImportFromDeviceRequest(
    string IpAddress,
    int? Port,
    bool UseHttps,
    string Username,
    string Password,
    DateTime? From,
    DateTime? To,
    int? MaxResults);

// DTO for /api/attendance/save-imported — events pre-fetched by the browser
public sealed class SaveImportedRequest
{
    public List<ImportedEvent> Events { get; set; } = new();
    public object? DeviceInfo { get; set; }
    public object? FaceRecognition { get; set; }
}

public sealed class ImportedEvent
{
    public string EmployeeNo { get; set; } = "";
    public string Time { get; set; } = "";
    public int Major { get; set; }
    public int Minor { get; set; }
    public string EventType { get; set; } = "check_in";
}

// DTO for leave approval/rejection requests via the API
public sealed record ApprovalBody(string? ReviewerEmail, string? Notes);

// DTOs for API key management
public sealed class CreateApiKeyRequest
{
    public string? Name { get; set; }
    public List<string>? Scopes { get; set; }
    public int RateLimitPerMinute { get; set; } = 60;
    public int? ExpiresInDays { get; set; }
}

public sealed class RevokeApiKeyRequest
{
    public string? Reason { get; set; }
}

// Exposed for integration tests via WebApplicationFactory<Program>
public partial class Program { }
