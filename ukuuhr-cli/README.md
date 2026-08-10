# Ukuu HR Sync Bridge — CLI

> Connect to Hikvision biometric devices, pull attendance records, and sync to Ukuu HR cloud

## Install

```bash
npm install -g ukuuhr
```

## Quick Start

```bash
# First-time setup: connect to your Hikvision device
ukuuhr connect

# Pull attendance records
ukuuhr attendance

# Sync records to Ukuu HR cloud
ukuuhr sync --once

# Probe device endpoints
ukuuhr probe
```

## Commands

| Command | Description |
|---------|-------------|
| `connect` | Connect to a Hikvision device and verify connection |
| `sync` | Fetch attendance events and push to Ukuu HR cloud (continuous or `--once`) |
| `attendance` | Pull attendance records from device and display locally |
| `probe` | Probe all ISAPI endpoints — discover what your device supports |
| `device-info` | Show device name, model, serial, firmware, capacity |
| `health` | Show CPU, memory, disk usage from the device |
| `curl` | Generate curl commands for every ISAPI endpoint |
| `test <path>` | Test a single ISAPI endpoint by path |
| `config` | Show current settings and config file location |
| `help` | Show help message |

## Options

| Option | Description |
|--------|-------------|
| `--config=path` | Path to settings.json |
| `--headless` | Non-interactive mode |
| `--once` | For sync: single sync then exit |
| `--json` | JSON output (probe/health/device-info/attendance) |
| `--days=N` | Date range in days for attendance (default: 7) |
| `--save=path` | Save attendance records to JSON file |
| `--timeout=N` | HTTP timeout in seconds (default: 15) |

## Examples

```bash
# Connect to device
ukuuhr connect

# Show attendance for last 7 days
ukuuhr attendance

# Show attendance for last 30 days
ukuuuhr attendance --days=30

# Export attendance as JSON
ukuuhr attendance --json --save=records.json

# Probe all device endpoints
ukuuhr probe

# One-shot cloud sync
ukuuhr sync --once

# Continuous cloud sync (every 5 min)
ukuuhr sync

# Get curl commands for manual testing
ukuuhr curl

# Device health check
ukuuhr health

# Device information
ukuuhr device-info

# Test specific endpoint
ukuuhr test /ISAPI/System/deviceInfo

# Show current configuration
ukuuhr config
```

## Configuration

Settings are stored in `~/.ukuuhr/settings.json`:

```json
{
  "deviceIp": "192.168.1.137",
  "devicePort": 80,
  "useHttps": false,
  "deviceUsername": "admin",
  "devicePassword": "",
  "cloudUrl": "https://ukuuhr.com",
  "apiKey": null,
  "syncIntervalMinutes": 5
}
```

On first run, `ukuuhr connect` will guide you through interactive setup.

## How It Works

The CLI communicates with Hikvision biometric terminals (like DS-K1T343EFWX) over the ISAPI protocol using HTTP Digest authentication.

### Attendance Record Fetching (3-Tier Fallback)

Not all Hikvision devices support the same endpoints. The CLI uses a 3-tier fallback strategy:

1. **AcsEvent JSON** (`/ISAPI/AccessControl/AcsEvent?format=json`) — preferred
2. **AcsEvent XML** (`/ISAPI/AccessControl/AcsEvent`) — fallback for devices that don't support `?format=json`
3. **AuditLog** (`/ISAPI/AccessControl/AuditLog/search`) — last resort for devices with limited ISAPI support

Run `ukuuhr probe` to discover which endpoints your specific device supports.

## Requirements

- Node.js 18+
- Network access to your Hikvision device
- Device credentials (username/password)

## License

MIT
