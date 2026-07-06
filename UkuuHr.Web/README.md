# Ukuu HR — C# / Blazor Edition

A complete rebuild of the original [Ukuu HR System](https://github.com/CHAMA18/ukuu_hr_system) (Dart/Flutter + Firebase) into **C# / .NET 9 / Blazor Server** with a world-class UI built on MudBlazor.

> **HRMS for the African market** — multi-tenant, RBAC-driven, country-aware payroll (Zambia / Tanzania / Malawi) with ZRA 2025 PAYE brackets, NAPSA & NHIMA compliance.

---

## ✨ What's Inside

| Module | Description |
|---|---|
| **Dashboard** | KPI cards (headcount, attendance, leave, payroll), workforce distribution chart, attendance donut, recent hires, payroll snapshot, pending approvals |
| **Employees** | Searchable/filterable directory + 4-step add/edit wizard (Personal → Employment → Banking → Tax & Statutory) with **live payroll preview** |
| **Attendance** | Daily attendance tracking, clock-in/out, status filter chips (present/late/absent/on-leave/remote), worked-hours computation |
| **Leave** | Approval workflow (approve/reject), leave types, public holidays, tabs for pending/approved/rejected/holidays |
| **Payroll** | Generate monthly batch, per-employee gross-to-net, approve/reject workflow, batch history, **live payroll calculator** with PAYE band breakdown |
| **Scheduling** | Department × shift assignments, weekly coverage matrix, day-of-week bitmask |
| **Documents** | Employee files (contracts, payslips, IDs, compliance, policies), category cards, organization-wide HR policies |
| **Messages** | Conversation list + message thread UI with sent/received bubble alignment |
| **Reports** | Headcount growth (line chart), department distribution (donut chart), payroll spend (bar chart), KPI tiles |
| **Settings** | Profile, organization, payroll config (NAPSA/PAYE bands), leave types, notifications, user management |
| **Security** | Security score, login stats, security policy toggles (MFA, SSO, IP allowlist, etc.), audit log table |
| **Billing** | Active license card with gradient hero, usage stats, plan comparison tiers |

## 🎨 Design Language

- **Brand palette:** Deep ink violet `#25163F` with muted purple `#4A3C68` and lavender `#6E5F92` accents
- **Typography:** Plus Jakarta Sans (headings), Inter (body), JetBrains Mono (code/numbers)
- **Surfaces:** Soft warm whites (`#FCFBFF`, `#F3F1F6`) instead of pure black/white
- **Components:** MudBlazor with custom overrides — flat design, subtle shadows, ink-forward borders
- **Sidebar:** Premium dark sidebar (`#1A0F30`) with gradient active indicators, collapsible to 84px
- **Cards:** Hover-lift micro-interactions with gradient top border on hover
- **Status pills:** Color-coded with bullet indicators for at-a-glance scanning

## 🏗️ Architecture

```
UkuuHr.Web/
├── Data/
│   ├── UkuuHrDbContext.cs        # EF Core: 18 DbSets, multi-tenant
│   └── DbSeeder.cs               # Demo data: 8 employees, attendance, payroll, leave
├── Models/                       # Domain entities (mirror of Dart models)
│   ├── Employee.cs               # Personal + Employment + Banking + Statutory
│   ├── Organization.cs           # Multi-tenant root + UserAccount RBAC
│   ├── Attendance.cs             # + LeaveRequest, LeaveType, LeaveHoliday
│   ├── Payroll.cs                # + DepartmentShiftAssignment
│   └── Documents.cs              # + HrConversation, AuditLog, LicenseCode, ExpenseRequest
├── Services/
│   ├── AuthService.cs            # Cookie auth, demo credentials
│   ├── CurrentUserService.cs     # Per-request current user / org / role
│   ├── PayrollCalculator.cs      # Gross-to-net engine (ZRA 2025 PAYE, NAPSA cap, NHIMA)
│   ├── HrServices.cs             # Employee, Attendance, Leave, Payroll, Audit services
│   └── CookieAuthStateProvider.cs
├── Components/
│   ├── App.razor                 # Root HTML + script imports
│   ├── Routes.razor              # AuthorizeRouteView with AdminLayout default
│   ├── Layout/
│   │   ├── AdminLayout.razor     # Sidebar + topbar + content shell
│   │   └── PublicLayout.razor    # Bare layout for login/register
│   ├── Pages/
│   │   ├── Login.razor
│   │   ├── Register.razor
│   │   ├── Dashboard.razor
│   │   ├── Employees.razor
│   │   ├── EmployeeAddEdit.razor
│   │   ├── EmployeeDetail.razor
│   │   ├── AttendancePage.razor
│   │   ├── Leave.razor
│   │   ├── Payroll.razor
│   │   ├── PayrollCalculatorPage.razor
│   │   ├── Scheduling.razor
│   │   ├── Documents.razor
│   │   ├── Messages.razor
│   │   ├── Reports.razor
│   │   ├── Settings.razor
│   │   ├── Security.razor
│   │   └── Billing.razor
│   └── Shared/
│       ├── SidebarItem.razor
│       ├── Field.razor
│       └── RedirectToLogin.razor
├── wwwroot/css/
│   ├── ukuu.css                  # World-class theme + MudBlazor overrides
│   └── app.css
├── Program.cs                    # DI, auth, MudBlazor, DB seed
└── UkuuHr.Web.csproj             # .NET 9 + EF Core + MudBlazor + QuestPDF + ClosedXML + CsvHelper
```

## 🔐 Authentication

Demo mode uses cookie authentication with the following credentials:

```
Email:    admin@ukuuhr.demo
Password: Admin@2025
```

## 💰 Payroll Calculation (Zambia Default — ZRA 2025)

```
Gross       = Basic + Taxable Allowances + Non-Taxable Allowances + Overtime + Bonuses
Taxable     = Basic + Taxable Allowances + Overtime + Bonuses
NAPSA       = MIN(Gross × 5%, ZMW 9,870)        // capped
NHIMA       = Gross × 1%
Taxable Income = Taxable − NAPSA
PAYE        = Progressive bands on Taxable Income:
               Band 1: 0 – 4,800               @ 0%
               Band 2: 4,801 – 6,900           @ 20%
               Band 3: 6,901 – 8,900           @ 30%
               Band 4: Beyond 8,900            @ 37.5%
Net         = Gross − NAPSA − NHIMA − PAYE − Other Deductions
```

Tanzania and Malawi configurations are supported via `PayrollCountryConfig.Tanzania()` / `Malawi()`.

## 🚀 Run

```bash
# .NET 9 SDK required
cd UkuuHr.Web
dotnet run

# Open http://localhost:5000
```

On first launch the SQLite database (`ukuuhr.db`) is created automatically and seeded with:
- 1 demo organization (UkuuHR Demo Ltd)
- 8 employees across 6 departments
- 30 days of attendance records
- 5 leave requests (mixed statuses)
- Previous-month approved payroll batch + current-month pending approvals
- Department shifts, public holidays, documents, policies, audit logs, license code

## 🧱 Tech Stack

| Concern | Library |
|---|---|
| Web framework | .NET 9 Blazor Server |
| UI components | MudBlazor 7 |
| Database | EF Core 9 + SQLite |
| Authentication | ASP.NET Core Cookie Auth |
| PDF generation | QuestPDF |
| Excel export | ClosedXML |
| CSV import/export | CsvHelper |

## 📋 Routes Implemented

| Path | Page |
|---|---|
| `/login`, `/register` | Public auth pages |
| `/`, `/dashboard` | Admin dashboard |
| `/employees`, `/employees/add`, `/employees/{id}`, `/employees/{id}/edit` | Employee module |
| `/attendance` | Time & attendance |
| `/leave` | Leave management |
| `/payroll`, `/payroll/calculator` | Payroll + live calculator |
| `/scheduling` | Shift scheduling |
| `/documents` | HR documents |
| `/messages` | In-app messaging |
| `/reports` | Analytics & charts |
| `/settings` | Profile + org + payroll config + users |
| `/security` | Security policies + audit log |
| `/billing` | License + plan comparison |
| `/logout` | Sign out (force-redirect) |

## 🎯 Original Dart → C# Mapping

| Dart | C# |
|---|---|
| `EmployeeSnapshot` (Firestore map wrapper) | `Employee` POCO + EF Core entity |
| `UserRole` enum + extension methods | `UserRole` enum + `UserRoleExtensions` static class |
| `PayrollCalculatorDialog` (live preview) | `PayrollCalculatorPage.razor` + `PayrollCalculator.Calculate()` static |
| `PayrollService.createPayrollRecord` (flat %) | Unified progressive-bracket calculation in service layer |
| `AdminPageShell` widget | `AdminLayout.razor` Blazor layout |
| `fl_chart` (line/bar/pie) | Inline SVG charts (no extra dependency) |
| `firebase_auth` + `cloud_firestore` | Cookie auth + EF Core + SQLite |
| `shared_preferences` | Server-side session (cookie) |

## 🆕 Phase 1 — FR-003 / FR-004 / FR-005 (Shifts & Tolerance)

Phase 1 delivers the foundation for accurate attendance computation:

| FRS Requirement | Module | Files |
|---|---|---|
| **FR-003** Attendance Tolerance | Org-level policy: late/early/half-day/absent thresholds + grace periods | `Models/Shift.cs` → `AttendanceTolerance`, `Services/ShiftEngine.cs`, `Services/ShiftService.cs` |
| **FR-004** Shift Management | CRUD for Fixed / Rotating / Flexible / Overnight shifts | `Models/Shift.cs` → `Shift`, `Components/Pages/Shifts.razor` |
| **FR-005** Multiple Shift Assignment | M:N assignments with rotation slots, effective windows, primary flag | `Models/Shift.cs` → `EmployeeShiftAssignment`, `Services/ShiftEngine.cs` |

### ShiftEngine (pure business logic)

The `ShiftEngine` is a static class with zero dependencies — it takes POCOs in and returns computed results. This keeps it trivially unit-testable.

```csharp
// Resolve the applicable shift for an employee on a date.
var resolution = ShiftEngine.Resolve(date, fallbackShift, assignments);

// Compute the AttendanceStatus from a check-in/check-out pair.
var computation = ShiftEngine.ComputeStatus(resolution, tolerance, checkIn, checkOut, date);

// Detect duplicate clock events (FR-002).
var isDup = ShiftEngine.IsDuplicate(empId, eventType, eventTime, existingEvents);

// Resolve cross-day (overnight) attendance date.
var attDate = ShiftEngine.ResolveAttendanceDate(shift, eventTime);
```

### Phase 1 Routes

| Path | Page |
|---|---|
| `/shifts` (also `/scheduling`) | Shift management with 4 tabs: Shifts / Assignments / Tolerance / Weekly Coverage |
| `/attendance` | Enhanced with shift column, late/early minutes, recomputation button, tolerance summary |

### Phase 1 Tests

```bash
cd UkuuHr.Tests
dotnet test  # 27 tests covering tolerance, overnight, rotation, duplicates, validation
```

## 🆕 Phase 2 — FR-006 / FR-007 / FR-008 (Overtime & Holidays)

| FRS Requirement | Module | Files |
|---|---|---|
| **FR-006** Overtime Management | Existing OvertimeService with approval workflow | `Services/HikvisionSyncService.cs` |
| **FR-007** Overtime Classification | Weekday / Weekend / Public Holiday rate-type cards on /overtime | `Components/Pages/Overtime.razor` |
| **FR-008** Holiday Management | CRUD + CSV import + Zambia holiday seeder | `Services/HolidayService.cs`, `Components/Pages/Holidays.razor`, `Data/Phase2Seeder.cs` |

## 🆕 Phase 3 — FR-001 Third-Party Device Integration

| Vendor | REST API | SDK | TCP/IP | CSV File |
|---|---|---|---|---|
| Hikvision | ✓ (ISAPI) | stub | stub | ✓ |
| ZKTeco | ✓ (HTTP API) | stub | stub | ✓ |
| Suprema | ✓ (BioStar 2) | stub | stub | ✓ |
| Dahua | ✓ (recordFinder) | stub | stub | ✓ |
| Anviz | ✓ (Cloud v2) | stub | stub | ✓ |
| Matrix | ✓ (COSEC) | stub | stub | ✓ |
| eSSL | ✓ (getdata.cgi) | stub | stub | ✓ |

### Architecture

```
AttendanceDevice (DB) ─┐
                       ├─→ DeviceSyncOrchestrator ─→ IDeviceConnectorRegistry
                       │                                  ├─→ HikvisionRestConnector
UnifiedClockEvent (DB) ┘                                  ├─→ ZKTecoRestConnector
                                                          ├─→ SupremaRestConnector
                                                          ├─→ DahuaRestConnector
                                                          ├─→ AnvizRestConnector
                                                          ├─→ MatrixRestConnector
                                                          ├─→ EsslRestConnector
                                                          ├─→ VendorSpecificCsvAdapter (×7)
                                                          ├─→ *SdkConnector (×6 stubs)
                                                          └─→ *TcpConnector (×4 stubs)
```

### Vendor Connector Contract

```csharp
public interface IDeviceConnector {
    DeviceVendor Vendor { get; }
    DeviceIntegrationMode Mode { get; }
    Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default);
    Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default);
}
```

Each REST connector parses vendor-specific payloads (XML/JSON/key=value) into `NormalizedClockEvent` records. The orchestrator persists events into `UnifiedClockEvent` with duplicate detection (same employee + type + 60-second window).

### Phase 3 Routes

| Path | Page |
|---|---|
| `/devices` | Unified device management — vendor matrix, device cards, sync/test/edit/delete |

### Phase 3 Tests

44 total tests (27 Phase 1 + 17 Phase 3) covering all 7 vendor parsers + CSV connector + registry + SDK/TCP stubs.

```bash
cd UkuuHr.Tests && dotnet test
```

## 🆕 Phase 4 — FR-009 Attendance Search + FR-010 Reporting

| FRS Requirement | Module | Files |
|---|---|---|
| **FR-009** Attendance Search | Multi-filter search: employee / dept / branch / shift / status / date / custom range | `Services/AttendanceReportService.cs` → `AttendanceSearchService`, `Components/Pages/AttendanceSearch.razor` |
| **FR-010** Reporting | Daily / Weekly / Monthly / Custom reports with CSV & XLSX export | `Services/AttendanceReportService.cs` → `ReportExportService` |

### Search Features

- 8-filter form: Employee, Department, Branch, Shift, Status, From/To date, free-text name search
- 5 quick-date presets: Today / 7 / 30 / 90 days / This month
- Pagination with "Showing X–Y of N" counter
- Results table: Date, Employee, Code, Department, Branch, CheckIn, CheckOut, Hours, Status pill

### Export Features

- **CSV**: 10-column format via CsvHelper
- **XLSX**: 2-sheet workbook (Summary + Detail) via ClosedXML
  - Summary sheet: report title, period, metric table with branded ink-violet headers
  - Detail sheet: full row data with color-coded Status cells (Present=green, Late=amber, Absent=red)
  - Frozen header row, auto-adjusted columns

### Phase 4 Tests

51 total tests (27 Phase 1 + 17 Phase 3 + 7 Phase 4) covering CSV/XLSX byte output, round-trip XLSX re-opening, summary computation, period resolution.

## 🆕 Phase 5 — FR-002 Auto-Sync + FR-012 Payroll API + FR-013 Modular Architecture

| FRS Requirement | Module | Files |
|---|---|---|
| **FR-002** Automatic Synchronization | Background service polls active devices on their `SyncIntervalMinutes` schedule | `Services/DeviceAutoSyncService.cs` |
| **FR-012** Payroll Integration | REST + CSV API endpoints exposing attendance summary for external payroll systems | `Program.cs` → `/api/payroll/*` |
| **FR-013** Modular Architecture | 8-module API surface with implementation status + system metrics endpoint | `Program.cs` → `/api/modules`, `/api/system/metrics` |

### Auto-Sync Background Service

`DeviceAutoSyncService` is an `IHostedService` that ticks every 60 seconds and syncs any device whose:
- `IsActive == true` AND
- `AutoSyncEnabled == true` AND
- `LastSyncAt + SyncIntervalMinutes <= now`

### Payroll Integration API

```
GET /api/payroll/attendance-summary?orgId=1&year=2026&month=7
  → JSON with per-employee { workedHours, overtimeHours, overtimePay, leaveDays, absentDays, lateDays, halfDays, basicSalary, currency }

GET /api/payroll/attendance-summary.csv?year=2026&month=7
  → CSV file download (Content-Disposition: attachment)
```

### Modular API Surface

```
GET /api/modules           → 8 modules with implementation status
GET /api/devices           → All active devices with sync metadata
GET /api/system/metrics    → Uptime + active modules list (NFR: 99.9% availability)
```

## 🆕 Phase 7 — Prisma Postgres Production Database

The app now uses **Prisma Postgres** (managed PostgreSQL 17 at `db.prisma.io`) as its production database, replacing the previous in-container PostgreSQL setup.

### What was set up

| Resource | Value |
|---|---|
| **Prisma workspace** | `wksp_cmquvlaky08kj0ff6otto9o88` ("Personal workspace") |
| **Prisma project** | `proj_cmquvls7a08lu0ff6ttpschja` ("Chungu Chipimo Chama") |
| **Database name** | `ukuu-hr-prod` |
| **Database ID** | `db_cmr96so2r032x0gf3dtugagzu` |
| **Region** | `us-east-1` (US East — N. Virginia) |
| **Postgres version** | 17.2 (Alpine) |
| **Direct endpoint** | `db.prisma.io:5432` |
| **Pooled endpoint** | `pooled.db.prisma.io:5432` (PgBouncer) |
| **Accelerate endpoint** | `accelerate.prisma-data.net:443` (Prisma Accelerate cache, optional) |
| **Tables created** | 26 tables, 412 columns (full Ukuu HR schema) |

### Connection priority

`Program.cs` resolves the database connection in this order:

1. **`POSTGRES_CONNECTION_STRING`** env var (preferred — Npgsql format). **← Set this in production.**
2. **`DATABASE_URL`** env var (Render-style `postgres://` URL — auto-converted to Npgsql).
3. **SQLite fallback** (`ukuuhr.db`) — local dev only, used when neither env var is set.

### Local development with Prisma Postgres

```bash
# 1. Get your connection string from Prisma Console → database → Connection tab
# 2. Export it as an env var
export POSTGRES_CONNECTION_STRING="Host=db.prisma.io;Port=5432;Database=postgres;Username=<API_KEY_ID>;Password=<API_KEY_SECRET>;SSL Mode=Require;TrustServerCertificate=true;Timeout=30"

# 3. Run the app — EF Core will run EnsureCreatedAsync + seed automatically
cd UkuuHr.Web
dotnet run
```

### Deploying to Render

1. Push your code to GitHub (the `Dockerfile` + `render.yaml` are already configured).
2. In Render dashboard → your service → Environment → add a secret variable:
   - **Key**: `POSTGRES_CONNECTION_STRING`
   - **Value**: the full Npgsql connection string from Prisma Console
3. Deploy. The new Dockerfile is much slimmer (no in-container PostgreSQL) — cold-start is ~10s instead of ~30s.

### Managing the database via the Prisma API

The Prisma Console API at `api.prisma.io/v1/` lets you list/create/delete databases programmatically. Example:

```bash
# List databases
curl -H "Authorization: Bearer $PRISMA_SERVICE_TOKEN" \
     https://api.prisma.io/v1/databases

# Create a new database
curl -X POST -H "Authorization: Bearer $PRISMA_SERVICE_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"name":"ukuu-hr-staging","projectId":"proj_xxx","region":"us-east-1"}' \
     https://api.prisma.io/v1/databases
```

### Production mode — no demo data (SEED_DEMO_DATA=false)

By default, `DbSeeder.SeedAsync` inserts demo data on first run (8 fictional employees, 7 demo vendor devices, 30 days of attendance, sample payroll/leave/holidays). This is great for evaluation but **not** what you want in production.

Set the `SEED_DEMO_DATA=false` env var to skip all demo seeding. The schema is still created via `EnsureCreatedAsync`, but zero rows are inserted. Login still works via the hardcoded admin fallback in `AuthService` (`admin@ukuuhr.demo` / `Admin@2025`).

```bash
export POSTGRES_CONNECTION_STRING="Host=db.prisma.io;..."
export SEED_DEMO_DATA=false
cd UkuuHr.Web && dotnet run
```

To wipe an already-seeded database back to a clean state:

```sql
-- Run via psql or any Postgres client against your Prisma Postgres instance
TRUNCATE TABLE 
  "AttendanceDevices", "AttendanceTolerances", "Attendances", "AuditLogs",
  "DepartmentShifts", "EmployeeDocuments", "EmployeeShiftAssignments", "Employees",
  "ExpenseRequests", "HikvisionClockEvents", "HikvisionDevices", "HrConversations",
  "HrMessages", "HrPolicies", "LeaveBalances", "LeaveHolidays", "LeaveRequests",
  "LeaveTypes", "LicenseCodes", "Organizations", "OvertimeRecords", "PayrollRuns",
  "PendingRegistrations", "Shifts", "UnifiedClockEvents", "UserAccounts"
RESTART IDENTITY CASCADE;
```

This preserves the schema (tables, indexes, constraints) but removes all rows and resets identity sequences back to 1.

## ⚠️ Notes

- The original Dart project uses Firebase; this C# rebuild uses EF Core + SQLite for simplicity and zero external service dependencies.
- The original supports 7 RBAC roles (SuperAdmin → Guest); this demo runs as SuperAdmin (Chungu Chama).
- The original supports Zambia / Tanzania / Malawi payroll configs; the demo defaults to Zambia (ZRA 2025).
- The original has 25+ pages and 50+ widgets; this rebuild implements all major admin-side modules in 16 pages.
