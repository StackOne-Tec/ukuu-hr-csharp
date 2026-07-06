using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
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

app.UseStaticFiles();
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
        new("employees", "Employee Management", true, "/api/employees"),
        new("attendance", "Attendance Management", true, "/api/attendance"),
        new("shifts", "Shift Management", true, "/api/shifts"),
        new("leave", "Leave Management", true, "/api/leave"),
        new("payroll", "Payroll Integration", true, "/api/payroll/attendance-summary"),
        new("reporting", "Reporting", true, "/api/reports"),
        new("notifications", "Notifications", false, null),
        new("devices", "Device Integration", true, "/api/devices")
    };
    return Results.Ok(new { organization = org?.Name, modules });
}).WithName("ModulesList");

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ───── Phase 5: FR-013 — Module info DTO for the modular API surface ─────
public sealed record ModuleInfo(string Key, string Name, bool Implemented, string? Endpoint);
