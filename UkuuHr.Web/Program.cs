using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using Scalar.AspNetCore;
using UkuuHr.Components;
using UkuuHr.Data;
using UkuuHr.Models;
using UkuuHr.Services;

// Use legacy timestamp behavior so DateTime is treated as 'timestamp without time zone'
// This avoids the "Cannot apply binary operation on types 'timestamp with time zone' and 'timestamp without time zone'" error
// when comparing DateTime properties with DateTime.Today/Now in LINQ queries.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ───────────── Database (PostgreSQL in prod, SQLite fallback for local dev) ─────────────
// Priority: explicit Npgsql connection string env var > DATABASE_URL (Render) > SQLite local file
// When running in our Docker container, entrypoint.sh exports POSTGRES_CONNECTION_STRING pointing to localhost.
// When running locally without env vars set, falls back to a SQLite file in the project root.
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
else if (!string.IsNullOrWhiteSpace(databaseUrl))
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
    return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={user};Password={pass};TrustServerCertificate=true;SSL Mode={(ssl ? "Require" : "Prefer")};Timeout=15;CommandTimeout=60";
}

static string ExtractHost(string connStr)
{
    var parts = connStr.Split(';');
    foreach (var p in parts)
    {
        var kv = p.Split('=', 2);
        if (kv.Length == 2 && kv[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
            return kv[1].Trim();
    }
    return "<unknown>";
}

// ───────────── Authentication ─────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "UkuuHr.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
        options.DetailedErrors = true;
    });

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
builder.Services.AddHttpClient("KeepAlive");

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

// Register the connector registry + orchestrator as singletons.
builder.Services.AddSingleton<UkuuHr.Services.Devices.IDeviceConnectorRegistry>(sp =>
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

// ───── Phase 4: FR-009 Attendance Search + FR-010 Reporting ─────
builder.Services.AddScoped<AttendanceSearchService>();
builder.Services.AddScoped<ReportExportService>();

// ───── Phase 13.5: Encryption at rest ─────
builder.Services.AddScoped<AesEncryptionService>();

// ───── FR-013: Notifications module ─────
builder.Services.AddScoped<NotificationService>();

// ───────────── KeepAlive: self-ping every 5 minutes to prevent Render free-tier spin-down ─────────────
builder.Services.AddHostedService<KeepAliveService>();

// ───── Phase 5: FR-002 — Automatic device sync background service ─────
builder.Services.AddHostedService<DeviceAutoSyncService>();

var app = builder.Build();

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
    OnPrepareResponse = ctx =>
    {
        // Default: no-cache for all static files unless overridden above
        if (!ctx.Context.Response.Headers.ContainsKey("Cache-Control"))
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ───── Phase 13.3: API key auth for external integration endpoints ─────
// External systems (HR, Payroll, ERP, mobile apps) authenticate via the
// X-API-Key header. The key is read from the UKUU_API_KEY env var.
// If UKUU_API_KEY is not set, API endpoints fall back to cookie auth.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        var apiKey = Environment.GetEnvironmentVariable("UKUU_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            var providedKey = ctx.Request.Headers["X-API-Key"].ToString();
            if (!string.IsNullOrEmpty(providedKey) && providedKey == apiKey)
            {
                // API key matches — create a generic identity for the request
                ctx.User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "api-client") },
                        "ApiKey"));
            }
            else if (!ctx.User.Identity?.IsAuthenticated == true)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Unauthorized. Provide X-API-Key header or sign in via cookie.\"}");
                return;
            }
        }
        // If UKUU_API_KEY is not set, API endpoints are open (development mode)
    }
    await next();
});

// Public health endpoint — used by Render health check + KeepAlive self-ping + UptimeRobot
var startTime = DateTime.UtcNow;
app.MapGet("/health", () => Results.Ok(new {
    status = "ok",
    timestamp = DateTime.UtcNow,
    uptime_seconds = (DateTime.UtcNow - startTime).TotalSeconds,
    db_host = ExtractHost(connectionString),
    env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "<not set>"
}));

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
    catch (Exception ex)
    {
        return Results.Json(new { status = "not_ready", timestamp = DateTime.UtcNow, db = "error", error = ex.Message },
            statusCode: 503);
    }
});

// Direct POST handler for login form (uses /auth/login to avoid conflict with Blazor's /login page route)
app.MapPost("/auth/login", async (HttpContext ctx, AuthService auth, ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var email = form["FormData.Email"].ToString();
    var password = form["FormData.Password"].ToString();
    var rememberMe = form["FormData.RememberMe"] == "true";

    logger.LogInformation("Login POST: email={Email}, rememberMe={RememberMe}", email, rememberMe);

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/login?error=1");
    }

    var success = await auth.SignInAsync(email, password, rememberMe);
    logger.LogInformation("Login result for {Email}: {Success}", email, success);

    if (success)
    {
        return Results.Redirect("/dashboard");
    }
    return Results.Redirect("/login?error=1");
});

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
});

// Phase 18: Auto-login endpoint for Blazor Server flows that can't access HttpContext.
// After account creation (which happens in a Blazor event handler without HttpContext),
// the app redirects here with forceLoad=true. This endpoint HAS a real HttpContext,
// so it can call AuthService.SignInAsync() to issue the auth cookie, then redirect
// to the dashboard.
app.MapGet("/auth/auto-login", async (HttpContext ctx, AuthService auth, ILogger<Program> logger) =>
{
    var email = ctx.Request.Query["email"].ToString();
    var password = ctx.Request.Query["password"].ToString();
    var returnUrl = ctx.Request.Query["returnUrl"].ToString();
    if (string.IsNullOrEmpty(returnUrl)) returnUrl = "/dashboard";

    logger.LogInformation("Auto-login: email={Email}", email);

    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        return Results.Redirect("/login?error=1");

    var success = await auth.SignInAsync(email, password, rememberMe: true);
    if (success)
    {
        logger.LogInformation("Auto-login success for {Email}", email);
        return Results.Redirect(returnUrl);
    }

    logger.LogWarning("Auto-login failed for {Email}", email);
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
            e.Status,
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
        emp.Status,
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
        workedHours = result.WorkedHours,
        dateKey = result.DateKey
    });
}).WithName("AttendanceClockOut");

// ═════════════════════════════════════════════════════════════════════════════
// MODULE 3: Shift Management
// ═════════════════════════════════════════════════════════════════════════════

// GET /api/shifts — list all shifts
app.MapGet("/api/shifts", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int? orgId) =>
{
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
            s.Kind,
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
    var shift = await svc.GetShiftAsync(oid, id);
    if (shift == null) return Results.NotFound(new { error = "Shift not found." });
    return Results.Ok(new
    {
        shift.Id,
        shift.Name,
        shift.Description,
        kind = shift.KindDisplay,
        shift.Kind,
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var deleted = await svc.DeleteShiftAsync(oid, id, actorEmail: null);
    if (!deleted) return Results.NotFound(new { error = "Shift not found." });
    return Results.Ok(new { status = "deactivated" });
}).WithName("ShiftsDelete");

// GET /api/shifts/assignments — list shift assignments
app.MapGet("/api/shifts/assignments", async (
    ShiftService svc,
    UkuuHrDbContext db,
    int? orgId,
    int? employeeId) =>
{
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
            r.Status,
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
        lr.Status,
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
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<LeaveRequest>();
    if (body == null) return Results.BadRequest(new { error = "Invalid request body." });

    body.OrganizationId = oid;
    var created = await svc.CreateAsync(body);
    return Results.Created($"/api/leave/{created.Id}", new { id = created.Id, status = created.Status });
}).WithName("LeaveCreate");

// POST /api/leave/{id}/approve — approve a leave request
app.MapPost("/api/leave/{id:int}/approve", async (
    HttpContext ctx,
    LeaveService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<ApprovalBody>();
    var reviewerEmail = body?.ReviewerEmail ?? "api-client";

    var result = await svc.ReviewAsync(oid, id, approve: true, reviewerEmail, notes: body?.Notes);
    if (!result) return Results.NotFound(new { error = "Leave request not found." });
    return Results.Ok(new { status = "approved", id });
}).WithName("LeaveApprove");

// POST /api/leave/{id}/reject — reject a leave request
app.MapPost("/api/leave/{id:int}/reject", async (
    HttpContext ctx,
    LeaveService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var body = await ctx.Request.ReadFromJsonAsync<ApprovalBody>();
    var reviewerEmail = body?.ReviewerEmail ?? "api-client";

    var result = await svc.ReviewAsync(oid, id, approve: false, reviewerEmail, notes: body?.Notes);
    if (!result) return Results.NotFound(new { error = "Leave request not found." });
    return Results.Ok(new { status = "rejected", id });
}).WithName("LeaveReject");

// POST /api/leave/{id}/cancel — cancel a leave request
app.MapPost("/api/leave/{id:int}/cancel", async (
    LeaveService svc,
    UkuuHrDbContext db,
    int id) =>
{
    var oid = (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
    if (oid == 0) return Results.NotFound(new { error = "No organization found." });

    var from = new DateTime(y, m, 1);
    var to = from.AddMonths(1).AddTicks(-1);

    var attendance = await db.Attendances
        .Where(a => a.OrganizationId == oid && a.Date >= from && a.Date <= to)
        .ToListAsync();
    var overtime = await db.OvertimeRecords
        .Where(o => o.OrganizationId == oid && o.Date >= from && o.Date <= to && o.Status != OvertimeStatus.Rejected)
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var org = await db.Organizations.FirstOrDefaultAsync();
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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
    var oid = orgId ?? (await db.Organizations.FirstOrDefaultAsync())?.Id ?? 0;
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ───── Phase 5: FR-013 — Module info DTO for the modular API surface ─────
public sealed record ModuleInfo(string Key, string Name, bool Implemented, string? Endpoint);

// DTO for leave approval/rejection requests via the API
public sealed record ApprovalBody(string? ReviewerEmail, string? Notes);

// Exposed for integration tests via WebApplicationFactory<Program>
public partial class Program { }
