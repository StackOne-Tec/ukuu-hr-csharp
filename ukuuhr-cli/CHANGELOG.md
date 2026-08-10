# Changelog

## 2.0.0 — 2026-08-10

### Added
- `connect` command — verify connection to Hikvision device with first-time setup
- `attendance` command — pull attendance records with 3-tier ISAPI fallback
- `sync` command — continuous or one-shot cloud sync
- `probe` command — discover device-supported ISAPI endpoints
- `device-info` command — show device name, model, serial, firmware
- `health` command — show CPU, memory, disk usage
- `curl` command — generate curl commands for all endpoints
- `test` command — test a single ISAPI endpoint
- `config` command — show/edit settings
- HTTP Digest authentication for Hikvision ISAPI
- Interactive first-time setup on `npm install` / `ukuuhr connect`
- `--json` output for programmatic use
- `--save=path` for exporting attendance records
- `--days=N` configurable date range
- Settings stored in `~/.ukuuhr/settings.json`
