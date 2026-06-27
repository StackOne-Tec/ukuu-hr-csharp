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

## ⚠️ Notes

- The original Dart project uses Firebase; this C# rebuild uses EF Core + SQLite for simplicity and zero external service dependencies.
- The original supports 7 RBAC roles (SuperAdmin → Guest); this demo runs as SuperAdmin (Chungu Chama).
- The original supports Zambia / Tanzania / Malawi payroll configs; the demo defaults to Zambia (ZRA 2025).
- The original has 25+ pages and 50+ widgets; this rebuild implements all major admin-side modules in 16 pages.
