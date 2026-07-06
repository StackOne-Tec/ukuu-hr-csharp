using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using UkuuHr.Components;
using UkuuHr.Data;
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
    options.AddPolicy("AdminOnly", p => p.RequireRole("SuperAdmin", "HrAdmin", "FinancePayrollAdmin", "HrOperator", "FinancePayroll"));
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

// ───────────── KeepAlive: self-ping every 5 minutes to prevent Render free-tier spin-down ─────────────
builder.Services.AddHostedService<KeepAliveService>();

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

// Public health endpoint — used by Render health check + KeepAlive self-ping + UptimeRobot
var startTime = DateTime.UtcNow;
app.MapGet("/health", () => Results.Ok(new {
    status = "ok",
    timestamp = DateTime.UtcNow,
    uptime_seconds = (DateTime.UtcNow - startTime).TotalSeconds,
    db_host = ExtractHost(connectionString),
    env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "<not set>"
}));

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
