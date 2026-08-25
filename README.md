# Ukuu HR — C# / Blazor Edition

![Build, Test & Publish Docker Image](https://github.com/StackOne-Tec/ukuu-hr-csharp/actions/workflows/docker-publish.yml/badge.svg)

A complete rebuild of the original [Ukuu HR System](https://github.com/CHAMA18/ukuu_hr_system) (Dart/Flutter + Firebase) into **C# / .NET 9 / Blazor Server** with a world-class UI built on MudBlazor.

> **HRMS for the African market** — multi-tenant, RBAC-driven, country-aware payroll (Zambia / Tanzania / Malawi) with ZRA 2025 PAYE brackets, NAPSA & NHIMA compliance.

---

## 🚀 Quick start

### Run via Docker (recommended)

The image is published automatically to GitHub Container Registry on every push to `main`:

```bash
docker pull ghcr.io/stackone-tec/ukuu-hr-csharp:latest

docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e SEED_DEMO_DATA=true \
  -e UKUU_ENCRYPTION_KEY=$(openssl rand -hex 32) \
  -e UKUU_API_KEY=$(openssl rand -hex 32) \
  ghcr.io/stackone-tec/ukuu-hr-csharp:latest
```

Then visit **http://localhost:8080** and log in with `admin@ukuuhr.demo` / `Admin@2025`.

The container falls back to SQLite if `POSTGRES_CONNECTION_STRING` is unset — fine for a quick demo, but for real use pass a Postgres connection string:

```bash
docker run --rm -p 8080:8080 \
  -e POSTGRES_CONNECTION_STRING="Host=...;Port=5432;Database=ukuuhr;Username=...;Password=..." \
  -e UKUU_ENCRYPTION_KEY=$(openssl rand -hex 32) \
  ghcr.io/stackone-tec/ukuu-hr-csharp:latest
```

### Run from source

Requires the **.NET 9 SDK** *and* the matching **.NET 9 runtime** — see the roll-forward gotcha below.

```bash
# 1. Sanity-check your SDK + runtime (you need Microsoft.AspNetCore.App 9.0.x)
dotnet --version                 # SDK 9.0.x (e.g. 9.0.316)
dotnet --list-runtimes | grep 'AspNetCore.App 9'   # must show 9.0.x

# 2. Run the web app (launchSettings.json: Development, port 5118)
cd UkuuHr.Web
dotnet run
```

Then open **http://localhost:5118** and log in with `admin@ukuuhr.demo` / `Admin@2025`.

On first launch the SQLite database (`UkuuHr.Web/ukuuhr.db`) is created and seeded automatically (demo org, 8 employees, attendance, payroll, leave, holidays). To use Postgres instead, set `POSTGRES_CONNECTION_STRING` — see [Configuration](#-configuration).

> **⚠️ Gotcha — do not roll forward to .NET 10.** The app targets `net9.0`. If only a .NET 10 runtime is installed and you force it (e.g. `DOTNET_ROLL_FORWARD=LatestMajor`), the app *starts* but Blazor interactivity silently breaks: the build-time static-assets manifest points `_framework/blazor.web.js` at the `.NET 9` shared-framework folder, which doesn't exist on a .NET 10-only machine, so the file 404s and the browser console logs `Blazor is not defined`. Pages render (server-side HTML) but buttons, forms and dialogs do nothing. Install the matching .NET 9 runtime (9.0.x) and run on it. On machines where the .NET 9 SDK lives outside `PATH` (e.g. `~/.dotnet`):
>
> ```bash
> export PATH="$HOME/.dotnet:$PATH"
> cd UkuuHr.Web && dotnet run
> ```

---

## 📦 Container registry

| Tag | Description |
|---|---|
| `ghcr.io/stackone-tec/ukuu-hr-csharp:latest` | Latest build from `main` |
| `ghcr.io/stackone-tec/ukuu-hr-csharp:sha-<7chars>` | Pinned to a specific commit |
| `ghcr.io/stackone-tec/ukuu-hr-csharp:v1.2.3` | Release tags (`git tag v1.2.3 && git push --tags`) |

Browse all tags: https://github.com/StackOne-Tec/ukuu-hr-csharp/pkgs/container/ukuu-hr-csharp

---

## ✨ What's Inside

| Module | Description |
|---|---|
| **Dashboard** | KPI cards (headcount, attendance, leave, payroll), workforce distribution chart, attendance donut, recent hires, payroll snapshot, pending approvals |
| **Employees** | Searchable/filterable directory + 4-step add/edit wizard (Personal → Employment → Banking → Tax & Statutory) with live payroll preview |
| **Attendance** | Daily attendance tracking, clock-in/out, status filter chips, worked-hours computation |
| **Leave** | Approval workflow (approve/reject), leave types, public holidays |
| **Payroll** | Generate monthly batch, per-employee gross-to-net, approve/reject workflow, batch history, live payroll calculator with PAYE band breakdown |
| **Scheduling** | Department × shift assignments, weekly coverage matrix, day-of-week bitmask |
| **Documents** | Employee files (contracts, payslips, IDs, compliance, policies), category cards, organization-wide HR policies |
| **Messages** | Conversation list + message thread UI with sent/received bubble alignment |
| **Reports** | Headcount growth (line chart), department distribution (donut chart), payroll spend (bar chart), KPI tiles |
| **Settings** | Profile, organization, payroll config (NAPSA/PAYE bands), leave types, notifications, user management |
| **Security** | Security score, login stats, security policy toggles (MFA, SSO, IP allowlist, etc.), audit log table |
| **Billing** | Active license card with gradient hero, usage stats, plan comparison tiers |
| **Devices** | Hikvision / ZKTeco / Suprema / Dahua / Anviz / Matrix / eSSL integration via REST API + CSV |

---

## 🏗️ Architecture

```
UkuuHr.Web/
├── Data/                         # EF Core: 29 DbSets, multi-tenant
├── Models/                       # Domain entities
├── Services/
│   ├── AuthService.cs            # Cookie auth, demo credentials
│   ├── PayrollCalculator.cs      # Gross-to-net engine (ZRA 2025 PAYE, NAPSA cap, NHIMA)
│   ├── HrServices.cs             # Employee, Attendance, Leave, Payroll, Audit services
│   ├── Hikvision/                # ISAPI protocol client + background sync
│   └── Devices/                  # Multi-vendor device connector registry
├── Components/
│   ├── Pages/                    # 20+ Blazor pages
│   └── Layout/                   # Admin + Public layouts
└── Program.cs                    # 2900+ line composition root: 100+ endpoints
```

---

## 🎨 Design Language

- **Brand palette:** Deep ink violet `#25163F` with muted purple `#4A3C68` and lavender `#6E5F92` accents
- **Typography:** Plus Jakarta Sans (headings), Inter (body), JetBrains Mono (code/numbers)
- **Surfaces:** Soft warm whites (`#FCFBFF`, `#F3F1F6`) instead of pure black/white
- **Components:** MudBlazor with custom overrides — flat design, subtle shadows, ink-forward borders

---

## 🧪 Tests

```bash
dotnet test UkuuHr.sln -c Release
```

1316 tests covering API integration, attendance reports, shift engine, vendor connectors, and the desktop Hikvision ISAPI parsers.

---

## 🆕 What's new (Aug 2026)

- **Multi-tenant isolation** — org resolution is principal-aware everywhere (API-key org → cookie user's org → first org); cross-org data access is blocked
- **API-key scopes enforced** — routes require the matching scope (Read/Write Employees/Attendance/Payroll, LeaveManagement, DeviceManagement, FullAccess); 403 with guidance otherwise
- **Branches & locations** — Branch entity, management UI (`/settings/branches`), employee assignment, report dimension
- **Subscriptions** — coupons provision/extend real licenses, activation endpoint, 30-day Professional trial at signup, plan employee limits enforced in Production
- **Payslips** — printable page per payroll run (`/payroll/{id}/payslip`) + email delivery via Resend
- **Email channel** — `RESEND_API_KEY` enables payslip/leave/overtime emails and admin alerts for device failures
- **Employee CSV/XLSX import & export**, deactivate/delete with audit trail
- **Attendance** — audited manual corrections, missing-punch detection, overnight-shift pairing, early-departure reporting
- **Device sync** — all vendor REST connectors persist events (ZKTeco, Suprema, Dahua, Anviz, Matrix, eSSL, CSV); desktop LAN bridge route fixed
- **Reliability** — probe-based idempotent schema migrations (heals missing columns on shared Postgres), encryption service never throws on missing key, `GET /api/admin/backup` JSON snapshot

---

## 🔧 Configuration

| Env var | Required | Description |
|---|---|---|
| `POSTGRES_CONNECTION_STRING` | No | Npgsql-format Postgres connection. Falls back to SQLite if unset. |
| `SEED_DEMO_DATA` | No | `true` seeds 8 demo employees + sample data. Default: `false`. |
| `UKUU_ENCRYPTION_KEY` | Recommended (prod) | 32-byte AES-256 key (64 hex chars) for encrypting bank account numbers / NRCs / TPINs. `openssl rand -hex 32`. If unset, the app falls back to a key file (`ukuu-master.key`), then a generated process-stable key — it never crashes. Set it to keep encryption stable across container rebuilds. |
| `UKUU_KEY_FILE` | No | Override path for the encryption key file (default: `ukuu-master.key` next to the app). |
| `UKUU_API_KEY` | No | 64-char hex key for `X-API-Key` auth on `/api/*` endpoints. If unset, API falls back to cookie auth. |
| `RESEND_API_KEY` | No | Resend.com API key for transactional emails (payslips, leave approvals). |

---

## 📜 License

MIT — see [LICENSE](LICENSE) if present, otherwise contact the maintainer.
