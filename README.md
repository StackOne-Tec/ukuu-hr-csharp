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

```bash
dotnet restore UkuuHr.sln
dotnet build UkuuHr.sln -c Release
cd UkuuHr.Web && dotnet bin/Release/net9.0/UkuuHr.Web.dll
```

Visit http://localhost:5000.

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

81 tests covering API integration, attendance reports, shift engine, and vendor connectors.

---

## 🔧 Configuration

| Env var | Required | Description |
|---|---|---|
| `POSTGRES_CONNECTION_STRING` | No | Npgsql-format Postgres connection. Falls back to SQLite if unset. |
| `SEED_DEMO_DATA` | No | `true` seeds 8 demo employees + sample data. Default: `false`. |
| `UKUU_ENCRYPTION_KEY` | **Yes (prod)** | 32-byte AES-256 key (64 hex chars) for encrypting bank account numbers / NRCs / TPINs. `openssl rand -hex 32` |
| `UKUU_API_KEY` | No | 64-char hex key for `X-API-Key` auth on `/api/*` endpoints. If unset, API falls back to cookie auth. |
| `RESEND_API_KEY` | No | Resend.com API key for transactional emails (payslips, leave approvals). |

---

## 📜 License

MIT — see [LICENSE](LICENSE) if present, otherwise contact the maintainer.
