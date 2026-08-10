/**
 * Ukuu HR Sync Bridge v2.0 — Node.js CLI
 *
 * A cross-platform CLI that connects to Hikvision biometric terminals via ISAPI.
 *
 * Commands:
 *   connect      Connect to a Hikvision device and verify connection
 *   sync         Fetch attendance events and push to Ukuu HR cloud (continuous or --once)
 *   attendance   Pull attendance records from device and display locally
 *   probe        Probe all ISAPI endpoints and report which ones the device supports
 *   device-info  Show device name, model, serial, firmware, capacity
 *   health       Show CPU, memory, disk usage from the device
 *   curl         Generate curl commands for every ISAPI endpoint (copy-paste to terminal)
 *   config       Show or edit the current settings
 *   test         Test a single ISAPI endpoint by path (e.g. /ISAPI/System/deviceInfo)
 *   help         Show this help message
 *
 * Global options:
 *   --config=path   Path to settings.json (default: ~/.ukuuhr/settings.json)
 *   --headless      Non-interactive mode (no prompts)
 *   --once          For sync: single sync then exit
 *   --json          Output as JSON (for probe/health/device-info/attendance)
 *   --days=N        Date range in days for attendance (default: 7)
 *   --save=path     Save attendance records to JSON file
 *   --timeout=N     HTTP timeout in seconds (default: 15)
 */

const fs = require('fs');
const path = require('path');
const { parseAcsEventJson, parseAcsEventXml, parseAuditLogXml, extractXmlValue } = require('./parser');
const { createIsapiClient } = require('./http-client');
const { loadOrCreateSettings, interactiveSetup, saveSettings, getSettingsPath, isValid } = require('./settings');
const C = require('./colors');

// ═══════════════════════════════════════════════════════════════════════
// Argument Parsing
// ═══════════════════════════════════════════════════════════════════════

const COMMANDS = ['connect', 'sync', 'attendance', 'probe', 'device-info', 'health', 'curl', 'test', 'config', 'help'];

function getCommand(args) {
  for (const arg of args) {
    if (COMMANDS.includes(arg)) return arg;
  }
  return 'connect'; // default command — establish connection
}

function getArgValue(args, prefix) {
  const match = args.find(a => a.startsWith(`${prefix}=`));
  return match ? match.substring(prefix.length + 1) : null;
}

function truncate(s, max = 200) {
  if (!s) return '';
  return s.length <= max ? s : s.substring(0, max) + '...';
}

function formatTime(timeStr) {
  try {
    const d = new Date(timeStr);
    if (!isNaN(d.getTime())) return d;
  } catch {}
  return null;
}

// ═══════════════════════════════════════════════════════════════════════
// Terminal Helpers
// ═══════════════════════════════════════════════════════════════════════

function writeLog(msg) {
  try { console.log(msg); } catch {}
}

function writeErr(msg) {
  try { console.error(`${C.RED}${msg}${C.RESET}`); } catch {}
}

function printBanner(subtitle) {
  const lines = [
    '',
    `  ${C.MAGENTA}${C.BOLD}╔═══════════════════════════════════════════════╗${C.RESET}`,
    `  ${C.MAGENTA}${C.BOLD}║${C.RESET}     ${C.WHITE}${C.BOLD}UKUU HR — SYNC BRIDGE v2.0${C.RESET}          ${C.MAGENTA}${C.BOLD}║${C.RESET}`,
    `  ${C.MAGENTA}${C.BOLD}╠═══════════════════════════════════════════════╣${C.RESET}`,
    `  ${C.MAGENTA}${C.BOLD}║${C.RESET}  ${C.CYAN}${subtitle}${C.RESET}                ${C.MAGENTA}${C.BOLD}║${C.RESET}`,
    `  ${C.MAGENTA}${C.BOLD}╚═══════════════════════════════════════════════╝${C.RESET}`,
    '',
  ];
  for (const line of lines) {
    try { console.log(line); } catch {}
  }
}

function printBar(label, pct) {
  const barLen = 20;
  const filled = Math.round(pct / 100 * barLen);
  const empty = barLen - filled;
  const color = pct < 70 ? C.GREEN : pct < 90 ? C.YELLOW : C.RED;
  const bar = '█'.repeat(filled) + '░'.repeat(empty);
  writeLog(`  ${C.CYAN}${label.padEnd(10)}${C.RESET} ${color}${bar}${C.RESET} ${pct.toFixed(1).padStart(5)}%`);
}

// ═══════════════════════════════════════════════════════════════════════
// Build AcsEvent Search XML
// ═══════════════════════════════════════════════════════════════════════

function buildAcsEventSearchXml(fromTime, toTime) {
  const from = fromTime.toISOString().replace('.000', '');
  const to = toTime.toISOString().replace('.000', '');
  return `<?xml version="1.0" encoding="UTF-8"?>
<AcsEventSearchDescription>
    <searchID>probe_test</searchID>
    <searchResultPosition>0</searchResultPosition>
    <maxResults>5</maxResults>
    <major>1</major>
    <minor>0</minor>
    <startTime>${from}</startTime>
    <endTime>${to}</endTime>
</AcsEventSearchDescription>`;
}

// ═══════════════════════════════════════════════════════════════════════
// Fetch Attendance Events — 3-tier fallback
// ═══════════════════════════════════════════════════════════════════════

async function fetchAttendanceEvents(client, fromTime, toTime) {
  let events = [];
  let tier1Error = null, tier2Error = null, tier3Error = null;

  // Tier 1: AcsEvent JSON (?format=json)
  try {
    const searchXml = buildAcsEventSearchXml(fromTime, toTime);
    const resp = await client.post('/ISAPI/AccessControl/AcsEvent?format=json', searchXml);

    if (resp.ok) {
      const body = await resp.text();
      events = (body.trimStart().startsWith('{') || body.trimStart().startsWith('['))
        ? parseAcsEventJson(body)
        : parseAcsEventXml(body);
      writeLog(`  ${C.GREEN}AcsEvent (JSON)${C.RESET}: ${events.length} records`);
    } else {
      const errBody = await client.readErrorBody(resp);
      tier1Error = `HTTP ${resp.status} — ${errBody}`;
      writeLog(`  ${C.YELLOW}AcsEvent (JSON)${C.RESET}: HTTP ${resp.status} — ${truncate(errBody, 100)}`);
    }
  } catch (ex) { tier1Error = ex.message; }

  // Tier 2: AcsEvent XML (no ?format=json)
  if (events.length === 0 && tier1Error) {
    try {
      const searchXml = buildAcsEventSearchXml(fromTime, toTime);
      const resp = await client.post('/ISAPI/AccessControl/AcsEvent', searchXml);

      if (resp.ok) {
        const body = await resp.text();
        events = parseAcsEventXml(body);
        writeLog(`  ${C.GREEN}AcsEvent (XML)${C.RESET}: ${events.length} records`);
      } else {
        const errBody = await client.readErrorBody(resp);
        tier2Error = `HTTP ${resp.status} — ${errBody}`;
        writeLog(`  ${C.YELLOW}AcsEvent (XML)${C.RESET}: HTTP ${resp.status}`);
      }
    } catch (ex) { tier2Error = ex.message; }
  }

  // Tier 3: AuditLog
  if (events.length === 0 && tier2Error) {
    try {
      const fromStr = encodeURIComponent(fromTime.toISOString());
      const toStr = encodeURIComponent(toTime.toISOString());
      const auditPath = `/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime=${fromStr}&endTime=${toStr}`;
      const resp = await client.get(auditPath);

      if (resp.ok) {
        const xml = await resp.text();
        events = parseAuditLogXml(xml);
        writeLog(`  ${C.GREEN}AuditLog${C.RESET}: ${events.length} records`);
      } else {
        const errBody = await client.readErrorBody(resp);
        tier3Error = `HTTP ${resp.status} — ${errBody}`;
        writeLog(`  ${C.RED}AuditLog${C.RESET}: HTTP ${resp.status}`);
      }
    } catch (ex) { tier3Error = ex.message; }
  }

  // Error summary
  if (events.length === 0 && tier1Error && tier2Error && tier3Error) {
    writeLog(`\n  ${C.RED}All event endpoints failed:${C.RESET}`);
    writeLog(`    AcsEvent JSON: ${tier1Error}`);
    writeLog(`    AcsEvent XML:  ${tier2Error}`);
    writeLog(`    AuditLog:      ${tier3Error}`);
    writeLog(`  ${C.CYAN}Tip: Run 'ukuuhr probe' to discover which endpoints your device supports.${C.RESET}`);
  }

  return events;
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: connect — verify connection to Hikvision device
// ═══════════════════════════════════════════════════════════════════════

async function cmdConnect(settings, timeout) {
  printBanner('CONNECT');

  const client = createIsapiClient(settings, timeout);
  const scheme = settings.useHttps ? 'https' : 'http';

  writeLog(`  ${C.CYAN}Device:    ${scheme}://${settings.deviceIp}:${settings.devicePort}${C.RESET}`);
  writeLog(`  ${C.CYAN}Username:  ${settings.deviceUsername}${C.RESET}`);
  writeLog(`  ${C.CYAN}Cloud:     ${settings.cloudUrl}${C.RESET}`);
  writeLog('');

  writeLog(`  Connecting to ${C.BOLD}${client.baseUrl}${C.RESET} ...`);

  try {
    const resp = await client.get('/ISAPI/System/deviceInfo');

    if (!resp.ok) {
      writeErr(`  Connection failed: HTTP ${resp.status}`);
      const errBody = await client.readErrorBody(resp);
      if (errBody) writeLog(`  ${C.DIM}${truncate(errBody, 300)}${C.RESET}`);
      writeLog(`\n  ${C.YELLOW}Tips:${C.RESET}`);
      writeLog(`    - Verify the device IP and port are correct`);
      writeLog(`    - Check that the device is powered on and reachable`);
      writeLog(`    - Ensure username/password are correct`);
      writeLog(`    - Run 'ukuuhr probe' to test all endpoints`);
      return 1;
    }

    const xml = await resp.text();
    const deviceName = extractXmlValue(xml, 'deviceName') || 'Unknown';
    const model = extractXmlValue(xml, 'model') || 'Unknown';
    const serial = extractXmlValue(xml, 'serialNumber') || 'N/A';
    const firmware = extractXmlValue(xml, 'firmwareVersion') || 'N/A';

    writeLog(`\n  ${C.GREEN}${C.BOLD}CONNECTED${C.RESET} to ${C.BOLD}${deviceName}${C.RESET} (${model})`);
    writeLog(`  ${C.CYAN}Serial:${C.RESET}     ${serial}`);
    writeLog(`  ${C.CYAN}Firmware:${C.RESET}  ${firmware}`);
    writeLog('');

    // Quick endpoint check
    writeLog(`  Checking event endpoints...`);
    const now = new Date();
    const yesterday = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    const events = await fetchAttendanceEvents(client, yesterday, now);

    if (events.length > 0) {
      writeLog(`\n  ${C.GREEN}Attendance endpoint: OK${C.RESET} (${events.length} records in last 24h)`);
    } else {
      writeLog(`\n  ${C.YELLOW}No records found in last 24h — device may be idle or endpoints need configuration${C.RESET}`);
      writeLog(`  Run ${C.CYAN}ukuuhr probe${C.RESET} to discover supported endpoints`);
    }

    writeLog(`\n  ${C.GREEN}${C.BOLD}Connection established!${C.RESET}`);
    writeLog(`  You can now run:`);
    writeLog(`    ${C.CYAN}ukuuhr attendance${C.RESET}     — Pull attendance records`);
    writeLog(`    ${C.CYAN}ukuuhr sync${C.RESET}           — Sync records to cloud`);
    writeLog(`    ${C.CYAN}ukuuhr device-info${C.RESET}    — Show device details`);
    writeLog(`    ${C.CYAN}ukuuhr health${C.RESET}         — Check device health`);

    return 0;
  } catch (ex) {
    writeErr(`  Connection failed: ${ex.message}`);
    writeLog(`\n  ${C.YELLOW}Tips:${C.RESET}`);
    writeLog(`    - Verify the device IP and port are correct`);
    writeLog(`    - Check that the device is powered on and reachable`);
    writeLog(`    - Run 'ukuuhr config' to update settings`);
    return 1;
  }
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: sync
// ═══════════════════════════════════════════════════════════════════════

async function cmdSync(settings, once, timeout) {
  printBanner('SYNC BRIDGE');

  const scheme = settings.useHttps ? 'https' : 'http';
  writeLog(`  ${C.CYAN}Device:   ${scheme}://${settings.deviceIp}:${settings.devicePort}${C.RESET}`);
  writeLog(`  ${C.CYAN}Username: ${settings.deviceUsername}${C.RESET}`);
  writeLog(`  ${C.CYAN}Cloud:    ${settings.cloudUrl}${C.RESET}`);
  writeLog(`  ${C.CYAN}Interval: ${settings.syncIntervalMinutes} min${C.RESET}`);
  writeLog('');

  if (!once) {
    writeLog(`  Press Ctrl+C to stop. Auto-sync every ${settings.syncIntervalMinutes} min.\n`);
  }

  const client = createIsapiClient(settings, timeout);
  let lastSync = new Date(0);

  const runOnce = async () => {
    try {
      await runSync(settings, client, lastSync, timeout);
      lastSync = new Date();
      writeLog(`  ${C.GREEN}[${ts()}]${C.RESET} Sync complete. Next in ${settings.syncIntervalMinutes} min.\n`);
    } catch (ex) {
      writeLog(`  ${C.RED}[${ts()}] ERROR:${C.RESET} ${ex.message}\n`);
    }
  };

  await runOnce();
  if (once) {
    writeLog('  Ukuu HR Sync Bridge stopped.');
    return 0;
  }

  // Continuous loop
  return new Promise((resolve) => {
    const interval = setInterval(async () => {
      await runOnce();
    }, settings.syncIntervalMinutes * 60 * 1000);

    process.on('SIGINT', () => {
      clearInterval(interval);
      writeLog('  Ukuu HR Sync Bridge stopped.');
      resolve(0);
    });
  });
}

async function runSync(settings, client, lastSync, timeout) {
  writeLog(`  [${ts()}] Connecting to ${client.baseUrl}...`);

  // Get device info
  let deviceName = 'Unknown', deviceModel = 'Unknown';
  try {
    const infoResp = await client.get('/ISAPI/System/deviceInfo');
    if (infoResp.ok) {
      const xml = await infoResp.text();
      deviceName = extractXmlValue(xml, 'deviceName') || 'Unknown';
      deviceModel = extractXmlValue(xml, 'model') || 'Unknown';
      writeLog(`  [${ts()}] Connected: ${C.BOLD}${deviceName}${C.RESET} (${deviceModel})`);
    }
  } catch {}

  // Fetch events
  const fromTime = lastSync.getTime() === 0 ? new Date(Date.now() - 7 * 24 * 60 * 60 * 1000) : lastSync;
  const toTime = new Date();
  const events = await fetchAttendanceEvents(client, fromTime, toTime);

  if (events.length === 0) {
    writeLog(`  [${ts()}] No new events (range: ${formatTimeStr(fromTime)} to ${formatTimeStr(toTime)}).`);
    return;
  }

  writeLog(`  [${ts()}] Fetched ${events.length} events. Pushing to cloud...`);

  // Push to cloud
  const payload = JSON.stringify({
    events,
    deviceInfo: { name: deviceName, model: deviceModel, serial: '' },
    faceRecognition: null,
  });

  try {
    const cloudUrl = settings.cloudUrl.replace(/\/$/, '') + '/api/attendance/save-imported';
    const headers = {
      'Content-Type': 'application/json',
      'Accept': 'application/json',
    };
    if (settings.apiKey) headers['X-API-Key'] = settings.apiKey;

    const resp = await fetch(cloudUrl, { method: 'POST', headers, body: payload });
    const cloudJson = await resp.text();

    if (resp.ok) {
      try {
        const data = JSON.parse(cloudJson);
        const fetched = data.eventsFetched || 0;
        const matched = data.employeesMatched || 0;
        const imported = data.recordsImported || 0;
        writeLog(`  [${ts()}] Cloud: ${fetched} fetched, ${matched} matched, ${imported} imported.`);
      } catch {
        writeLog(`  [${ts()}] Cloud OK (response: ${truncate(cloudJson, 200)})`);
      }
    } else {
      writeLog(`  ${C.RED}[${ts()}] Cloud error HTTP ${resp.status}:${C.RESET} ${truncate(cloudJson, 200)}`);
    }
  } catch (ex) {
    writeLog(`  ${C.RED}[${ts()}] Cloud push failed:${C.RESET} ${ex.message}`);
  }
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: attendance — pull and display attendance records
// ═══════════════════════════════════════════════════════════════════════

async function cmdAttendance(settings, args, jsonOutput, timeout) {
  printBanner('ATTENDANCE RECORDS');

  const days = parseInt(getArgValue(args, '--days'), 10) || 7;
  const savePath = getArgValue(args, '--save');
  const fromTime = new Date(Date.now() - days * 24 * 60 * 60 * 1000);
  const toTime = new Date();

  const client = createIsapiClient(settings, timeout);

  writeLog(`  Fetching attendance records from ${C.BOLD}${client.baseUrl}${C.RESET}`);
  writeLog(`  Date range: ${formatDate(fromTime)} to ${formatDate(toTime)} (${days} days)\n`);

  // Get device info
  let deviceName = 'Unknown', deviceModel = 'Unknown';
  try {
    const infoResp = await client.get('/ISAPI/System/deviceInfo');
    if (infoResp.ok) {
      const xml = await infoResp.text();
      deviceName = extractXmlValue(xml, 'deviceName') || 'Unknown';
      deviceModel = extractXmlValue(xml, 'model') || 'Unknown';
      writeLog(`  Device: ${C.BOLD}${deviceName}${C.RESET} (${deviceModel})\n`);
    }
  } catch {}

  // Fetch events using 3-tier fallback
  const events = await fetchAttendanceEvents(client, fromTime, toTime);

  if (events.length === 0) {
    writeLog(`  ${C.YELLOW}No attendance records found.${C.RESET}`);
    writeLog(`  ${C.DIM}Try increasing the date range: ukuuhr attendance --days=30${C.RESET}`);
    return 0;
  }

  // JSON output
  if (jsonOutput) {
    const json = JSON.stringify({
      device: { name: deviceName, model: deviceModel, ip: settings.deviceIp },
      range: { from: fromTime.toISOString(), to: toTime.toISOString(), days },
      totalEvents: events.length,
      events,
    }, null, 2);
    console.log(json);

    if (savePath) {
      fs.writeFileSync(savePath, json);
      writeLog(`  ${C.GREEN}Saved to: ${savePath}${C.RESET}`);
    }
    return 0;
  }

  // Display attendance table
  writeLog(`  ${C.BOLD}ATTENDANCE RECORDS${C.RESET}  (${events.length} total)\n`);

  // Group by date
  const byDate = {};
  for (const e of events) {
    const d = formatTime(e.time);
    const key = d ? d.toISOString().split('T')[0] : 'unknown';
    if (!byDate[key]) byDate[key] = [];
    byDate[key].push(e);
  }

  const sortedDates = Object.keys(byDate).sort().reverse();

  for (const dateKey of sortedDates) {
    const dateEvents = byDate[dateKey].sort((a, b) => a.time.localeCompare(b.time));
    const checkIns = dateEvents.filter(e => e.eventType === 'check_in').length;
    const checkOuts = dateEvents.filter(e => e.eventType === 'check_out').length;

    writeLog(`  ${C.MAGENTA}${C.BOLD}${dateKey}${C.RESET}  ${C.GREEN}${checkIns} check-ins${C.RESET}  ${C.CYAN}${checkOuts} check-outs${C.RESET}  ${C.DIM}${dateEvents.length} total${C.RESET}`);
    writeLog(`  ${'─'.repeat(70)}`);
    writeLog(`  ${C.DIM}${'Employee'.padEnd(12)}${C.RESET} ${C.DIM}${'Time'.padEnd(10)}${C.RESET} ${C.DIM}${'Type'.padEnd(12)}${C.RESET} ${C.DIM}Minor${C.RESET}`);

    for (const e of dateEvents) {
      const d = formatTime(e.time);
      const timeOnly = d ? d.toTimeString().substring(0, 8) : (e.time || '?').slice(-8);
      const typeColor = e.eventType === 'check_in' ? C.GREEN : C.CYAN;
      const typeLabel = e.eventType === 'check_in' ? 'CHECK IN' : 'CHECK OUT';

      writeLog(`  ${e.employeeNo.padEnd(12)} ${timeOnly.padEnd(10)} ${typeColor}${typeLabel.padEnd(12)}${C.RESET} ${C.DIM}${e.minor}${C.RESET}`);
    }
    writeLog('');
  }

  // Employee summary
  writeLog(`  ${C.BOLD}EMPLOYEE SUMMARY${C.RESET}`);
  writeLog(`  ${'─'.repeat(50)}`);

  const byEmployee = {};
  for (const e of events) {
    if (!byEmployee[e.employeeNo]) byEmployee[e.employeeNo] = [];
    byEmployee[e.employeeNo].push(e);
  }

  const sortedEmployees = Object.entries(byEmployee)
    .sort((a, b) => b[1].length - a[1].length);

  writeLog(`  ${C.DIM}${'Employee'.padEnd(12)}${C.RESET} ${C.DIM}${'Total'.padEnd(8)}${C.RESET} ${C.DIM}${'Check-ins'.padEnd(12)}${C.RESET} ${C.DIM}Check-outs${C.RESET}`);
  for (const [empNo, empEvents] of sortedEmployees) {
    const ins = empEvents.filter(e => e.eventType === 'check_in').length;
    const outs = empEvents.filter(e => e.eventType === 'check_out').length;
    writeLog(`  ${empNo.padEnd(12)} ${String(empEvents.length).padEnd(8)} ${C.GREEN}${String(ins).padEnd(12)}${C.RESET} ${C.CYAN}${outs}${C.RESET}`);
  }
  writeLog(`  ${'─'.repeat(50)}`);
  writeLog(`  ${C.BOLD}${sortedEmployees.length} unique employees${C.RESET}, ${events.length} total records`);

  // Save to file if requested
  if (savePath) {
    const json = JSON.stringify({
      device: { name: deviceName, model: deviceModel, ip: settings.deviceIp },
      range: { from: fromTime.toISOString(), to: toTime.toISOString(), days },
      totalEvents: events.length,
      events,
    }, null, 2);
    fs.writeFileSync(savePath, json);
    writeLog(`\n  ${C.GREEN}Saved to: ${savePath}${C.RESET}`);
  }

  return 0;
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: probe
// ═══════════════════════════════════════════════════════════════════════

function defineProbes(settings) {
  const now = new Date();
  const yesterday = new Date(now.getTime() - 24 * 60 * 60 * 1000);
  const searchXml = buildAcsEventSearchXml(yesterday, now);
  const fromStr = encodeURIComponent(yesterday.toISOString());
  const toStr = encodeURIComponent(now.toISOString());

  return [
    // System
    { category: 'System', name: 'Device Info', method: 'GET', path: '/ISAPI/System/deviceInfo', postBody: null },
    { category: 'System', name: 'Capabilities', method: 'GET', path: '/ISAPI/System/capabilities', postBody: null },
    { category: 'System', name: 'Device Status (JSON)', method: 'GET', path: '/ISAPI/System/status?format=json', postBody: null },
    { category: 'System', name: 'Device Status (XML)', method: 'GET', path: '/ISAPI/System/status', postBody: null },
    { category: 'System', name: 'Device Time', method: 'GET', path: '/ISAPI/System/time', postBody: null },
    { category: 'System', name: 'Network Config', method: 'GET', path: '/ISAPI/System/networkInterfaces', postBody: null },
    { category: 'System', name: 'Device Capacity', method: 'GET', path: '/ISAPI/System/deviceCapacity', postBody: null },

    // Access Control
    { category: 'Access Control', name: 'AcsEvent (JSON)', method: 'POST', path: '/ISAPI/AccessControl/AcsEvent?format=json', postBody: searchXml },
    { category: 'Access Control', name: 'AcsEvent (XML)', method: 'POST', path: '/ISAPI/AccessControl/AcsEvent', postBody: searchXml },
    { category: 'Access Control', name: 'AuditLog Search', method: 'GET', path: `/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime=${fromStr}&endTime=${toStr}`, postBody: null },
    { category: 'Access Control', name: 'AuditLog (no params)', method: 'GET', path: '/ISAPI/AccessControl/AuditLog/search', postBody: null },
    { category: 'Access Control', name: 'Door Status', method: 'GET', path: '/ISAPI/AccessControl/Door/status', postBody: null },

    // People
    { category: 'People', name: 'All Persons', method: 'GET', path: '/ISAPI/AccessControl/UserInfo/Search?format=json', postBody: null },

    // Events
    { category: 'Events', name: 'Event Notification Caps', method: 'GET', path: '/ISAPI/Event/notification/capabilities', postBody: null },

    // Security
    { category: 'Security', name: 'Security Caps', method: 'GET', path: '/ISAPI/Security/capabilities', postBody: null },
  ];
}

async function cmdProbe(settings, jsonOutput, timeout) {
  printBanner('ISAPI ENDPOINT PROBE');

  const client = createIsapiClient(settings, timeout);
  writeLog(`  Probing ${C.BOLD}${client.baseUrl}${C.RESET} ...\n`);

  const probes = defineProbes(settings);
  const results = [];

  for (const probe of probes) {
    writeLog(`  ${C.DIM}Testing${C.RESET} ${probe.name} (${probe.method} ${probe.path})...`);

    const start = Date.now();
    let statusCode = 0;
    let errorMsg = null;
    let body = '';

    try {
      let resp;
      if (probe.method === 'GET') {
        resp = await client.get(probe.path);
      } else if (probe.method === 'POST' && probe.postBody) {
        resp = await client.post(probe.path, probe.postBody);
      }

      if (resp) {
        statusCode = resp.status;
        body = truncate(await resp.text(), 2000);
      }
    } catch (ex) {
      errorMsg = ex.message;
    }

    const elapsedMs = Date.now() - start;
    const isOk = statusCode >= 200 && statusCode < 300;
    const isUnsupported = statusCode === 404 || statusCode === 400;

    results.push({
      name: probe.name,
      category: probe.category,
      method: probe.method,
      path: probe.path,
      statusCode,
      errorMsg,
      body,
      elapsedMs,
      isOk,
      isUnsupported,
      isFailed: statusCode >= 400 || !!errorMsg,
    });

    const statusColor = isOk ? C.GREEN : isUnsupported ? C.YELLOW : C.RED;
    const icon = isOk ? 'OK' : statusCode > 0 ? `${statusCode}` : 'FAIL';
    writeLog(`    ${statusColor}${C.BOLD}${icon}${C.RESET}  ${elapsedMs}ms  ${probe.name}`);
  }

  writeLog('');

  const ok = results.filter(r => r.isOk).length;
  const fail = results.filter(r => r.isFailed).length;
  const unsup = results.filter(r => r.isUnsupported).length;

  if (jsonOutput) {
    console.log(JSON.stringify({
      baseUrl: client.baseUrl,
      probedAt: new Date().toISOString(),
      total: results.length,
      ok, failed: fail, unsupported: unsup,
      probes: results.map(r => ({
        name: r.name, category: r.category, method: r.method, path: r.path,
        statusCode: r.statusCode, elapsedMs: r.elapsedMs,
        success: r.isOk, error: r.errorMsg,
        responsePreview: truncate(r.body, 500),
      })),
    }, null, 2));
    return 0;
  }

  // Print full table
  writeLog(`  ${C.BOLD}PROBE RESULTS${C.RESET}`);
  writeLog(`  ${'─'.repeat(80)}`);

  let currentCat = '';
  for (const r of results) {
    if (r.category !== currentCat) {
      currentCat = r.category;
      writeLog(`  ${C.MAGENTA}${C.BOLD}${currentCat}${C.RESET}`);
    }

    const statusColor = r.isOk ? C.GREEN : r.isUnsupported ? C.YELLOW : C.RED;
    const icon = r.isOk ? 'OK' : r.statusCode > 0 ? `${r.statusCode}` : 'FAIL';
    writeLog(`    ${statusColor}${icon.padStart(5)}${C.RESET}  ${String(r.elapsedMs).padStart(5)}ms  ${C.CYAN}${r.method.padEnd(5)}${C.RESET}  ${r.name}`);
    writeLog(`    ${' '.repeat(14)}${C.DIM}${r.path}${C.RESET}`);

    if (!r.isOk && r.errorMsg) {
      writeLog(`    ${' '.repeat(14)}${C.RED}${truncate(r.errorMsg, 80)}${C.RESET}`);
    }
  }

  writeLog(`  ${'─'.repeat(80)}`);
  writeLog(`  ${C.GREEN}${ok} OK${C.RESET}  ${C.RED}${fail} Failed${C.RESET}  ${C.YELLOW}${unsup} Unsupported${C.RESET}  / ${results.length} total`);
  writeLog('');

  // Recommendation
  const acsJson = results.find(r => r.name === 'AcsEvent (JSON)');
  const acsXml = results.find(r => r.name === 'AcsEvent (XML)');
  const auditLog = results.find(r => r.name === 'AuditLog Search');

  writeLog(`  ${C.BOLD}RECOMMENDATION${C.RESET}`);
  if (acsJson?.isOk) {
    writeLog(`    ${C.GREEN}Your device supports AcsEvent with ?format=json — this is the preferred endpoint.${C.RESET}`);
  } else if (acsXml?.isOk) {
    writeLog(`    ${C.YELLOW}Your device does NOT support ?format=json, but AcsEvent XML works.${C.RESET}`);
  } else if (auditLog?.isOk) {
    writeLog(`    ${C.YELLOW}AcsEvent is not supported. Use AuditLog as the event source.${C.RESET}`);
  } else {
    writeLog(`    ${C.RED}No event endpoints are working. Check credentials and network.${C.RESET}`);
  }

  writeLog(`\n  Run ${C.CYAN}ukuuhr curl${C.RESET} to get terminal commands for manual testing.`);
  return fail === results.length ? 1 : 0;
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: device-info
// ═══════════════════════════════════════════════════════════════════════

async function cmdDeviceInfo(settings, jsonOutput, timeout) {
  printBanner('DEVICE INFO');

  const client = createIsapiClient(settings, timeout);

  try {
    const resp = await client.get('/ISAPI/System/deviceInfo');
    if (!resp.ok) {
      writeErr(`Failed: HTTP ${resp.status}`);
      return 1;
    }

    const xml = await resp.text();

    if (jsonOutput) {
      console.log(xml);
      return 0;
    }

    const fields = [
      'deviceName', 'deviceID', 'model', 'serialNumber', 'macAddress',
      'firmwareVersion', 'hardwareVersion', 'deviceType', 'maxUsers', 'maxFingers', 'maxFaces', 'maxCards'
    ];

    writeLog(`  ${C.BOLD}DEVICE INFORMATION${C.RESET}`);
    writeLog(`  ${'─'.repeat(50)}`);
    for (const field of fields) {
      const value = extractXmlValue(xml, field);
      if (value) {
        const label = field.charAt(0).toUpperCase() + field.slice(1);
        writeLog(`  ${C.CYAN}${label.padEnd(18)}${C.RESET} ${value}`);
      }
    }
    writeLog(`  ${'─'.repeat(50)}`);

    // Try capabilities
    try {
      const capResp = await client.get('/ISAPI/System/capabilities');
      if (capResp.ok) {
        const capXml = await capResp.text();
        writeLog(`\n  ${C.BOLD}CAPABILITIES${C.RESET} (raw XML available with --json)`);
        writeLog(`  ${C.DIM}${truncate(capXml, 500)}${C.RESET}`);
      }
    } catch {}

    return 0;
  } catch (ex) {
    writeErr(`Connection failed: ${ex.message}`);
    return 1;
  }
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: health
// ═══════════════════════════════════════════════════════════════════════

async function cmdHealth(settings, jsonOutput, timeout) {
  printBanner('DEVICE HEALTH');

  const client = createIsapiClient(settings, timeout);

  try {
    let resp = await client.get('/ISAPI/System/status?format=json');
    if (!resp.ok) {
      resp = await client.get('/ISAPI/System/status');
    }

    if (!resp.ok) {
      writeErr(`Failed: HTTP ${resp.status}`);
      return 1;
    }

    const body = await resp.text();

    if (jsonOutput) {
      console.log(body);
      return 0;
    }

    try {
      const data = JSON.parse(body);
      const status = data.DeviceStatus || data.deviceStatus;
      if (status) {
        writeLog(`  ${C.BOLD}DEVICE HEALTH${C.RESET}`);
        writeLog(`  ${'─'.repeat(40)}`);

        if (status.currentCpuUsage !== undefined) printBar('CPU', parseFloat(status.currentCpuUsage));
        if (status.currentMemoryUsage !== undefined) printBar('Memory', parseFloat(status.currentMemoryUsage));
        if (status.currentDiskUsage !== undefined) printBar('Disk', parseFloat(status.currentDiskUsage));
        if (status.upTime !== undefined) {
          const uptime = parseInt(status.upTime, 10);
          writeLog(`  ${C.CYAN}Uptime${C.RESET}           ${uptime}s (${Math.floor(uptime / 3600)}h ${Math.floor((uptime % 3600) / 60)}m)`);
        }

        writeLog(`  ${'─'.repeat(40)}`);
      } else {
        writeLog(`  ${C.DIM}${truncate(body, 500)}${C.RESET}`);
      }
    } catch {
      writeLog(`  ${C.DIM}${truncate(body, 500)}${C.RESET}`);
    }

    return 0;
  } catch (ex) {
    writeErr(`Connection failed: ${ex.message}`);
    return 1;
  }
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: curl
// ═══════════════════════════════════════════════════════════════════════

async function cmdCurl(settings, timeout) {
  printBanner('CURL COMMANDS');

  const client = createIsapiClient(settings, timeout);
  const probes = defineProbes(settings);

  writeLog(`  # ISAPI Endpoint Commands for ${settings.deviceIp}`);
  writeLog(`  # Device: ${client.baseUrl}`);
  writeLog(`  # Generated: ${new Date().toISOString()}`);
  writeLog('');

  for (const probe of probes) {
    const curl = client.generateCurl(probe.path, probe.method, probe.postBody);
    writeLog(`  # ${probe.name} (${probe.method} ${probe.path})`);
    writeLog(`  ${curl}`);
    writeLog('');
  }

  writeLog(`  ${C.CYAN}Tip:${C.RESET} Run these from any terminal on the same network.`);
  writeLog(`  ${C.CYAN}Tip:${C.RESET} Successful commands (HTTP 200) confirm device support.`);

  return 0;
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: test
// ═══════════════════════════════════════════════════════════════════════

async function cmdTest(settings, args, timeout) {
  const testArgs = args.slice(args.indexOf('test') + 1);
  const testPath = testArgs[0] || '/ISAPI/System/deviceInfo';

  printBanner('ENDPOINT TEST');

  const client = createIsapiClient(settings, timeout);
  writeLog(`  Testing: ${C.BOLD}${client.baseUrl}${testPath}${C.RESET}\n`);

  const start = Date.now();
  try {
    const resp = await client.get(testPath);
    const elapsedMs = Date.now() - start;
    const body = await resp.text();

    const statusColor = resp.ok ? C.GREEN : C.RED;
    writeLog(`  ${statusColor}${C.BOLD}HTTP ${resp.status}${C.RESET} ${resp.statusText}  (${elapsedMs}ms)`);
    writeLog(`  ${'─'.repeat(60)}`);
    writeLog(`  ${C.DIM}${truncate(body, 1000)}${C.RESET}`);

    writeLog(`\n  ${C.BOLD}Equivalent curl:${C.RESET}`);
    writeLog(`  ${client.generateCurl(testPath)}`);

    return resp.ok ? 0 : 1;
  } catch (ex) {
    const elapsedMs = Date.now() - start;
    writeLog(`  ${C.RED}${C.BOLD}FAILED${C.RESET} (${elapsedMs}ms): ${ex.message}`);
    return 1;
  }
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: config
// ═══════════════════════════════════════════════════════════════════════

function cmdConfig(settings, configPath) {
  printBanner('CONFIGURATION');

  const filePath = getSettingsPath(configPath);

  writeLog(`  ${C.BOLD}Settings file:${C.RESET} ${filePath}`);
  writeLog(`  ${'─'.repeat(50)}`);
  writeLog(`  ${C.CYAN}Device IP:${C.RESET}       ${settings.deviceIp}`);
  writeLog(`  ${C.CYAN}Port:${C.RESET}            ${settings.devicePort}`);
  writeLog(`  ${C.CYAN}HTTPS:${C.RESET}           ${settings.useHttps}`);
  writeLog(`  ${C.CYAN}Username:${C.RESET}        ${settings.deviceUsername}`);
  writeLog(`  ${C.CYAN}Password:${C.RESET}        ${'*'.repeat(Math.min((settings.devicePassword || '').length, 20))}`);
  writeLog(`  ${C.CYAN}Cloud URL:${C.RESET}       ${settings.cloudUrl}`);
  writeLog(`  ${C.CYAN}API Key:${C.RESET}         ${(!settings.apiKey ? '(not set)' : '*'.repeat(Math.min(settings.apiKey.length, 20)))}`);
  writeLog(`  ${C.CYAN}Sync Interval:${C.RESET}   ${settings.syncIntervalMinutes} min`);
  writeLog(`  ${'─'.repeat(50)}`);
  writeLog(`\n  Edit ${filePath} to change settings.`);

  return 0;
}

// ═══════════════════════════════════════════════════════════════════════
// COMMAND: help
// ═══════════════════════════════════════════════════════════════════════

function cmdHelp() {
  printBanner('HELP');

  writeLog(`
  ${C.BOLD}USAGE${C.RESET}
    ukuuhr <command> [options]

  ${C.BOLD}COMMANDS${C.RESET}
    ${C.CYAN}connect${C.RESET}      Connect to a Hikvision device and verify connection
    ${C.CYAN}sync${C.RESET}         Fetch attendance events and push to cloud (continuous or --once)
    ${C.CYAN}attendance${C.RESET}   Pull attendance records from device and display locally
    ${C.CYAN}probe${C.RESET}        Probe all ISAPI endpoints — discover what your device supports
    ${C.CYAN}device-info${C.RESET}  Show device name, model, serial, firmware, capacity
    ${C.CYAN}health${C.RESET}       Show CPU, memory, disk usage from the device
    ${C.CYAN}curl${C.RESET}         Generate curl commands for every ISAPI endpoint
    ${C.CYAN}test${C.RESET} <path>  Test a single ISAPI endpoint by path
    ${C.CYAN}config${C.RESET}       Show current settings and config file location
    ${C.CYAN}help${C.RESET}         Show this help message

  ${C.BOLD}OPTIONS${C.RESET}
    ${C.CYAN}--config=path${C.RESET}   Path to settings.json
    ${C.CYAN}--headless${C.RESET}      Non-interactive mode
    ${C.CYAN}--once${C.RESET}          For sync: single sync then exit
    ${C.CYAN}--json${C.RESET}          JSON output (probe/health/device-info/attendance)
    ${C.CYAN}--days=N${C.RESET}       Date range in days for attendance (default: 7)
    ${C.CYAN}--save=path${C.RESET}    Save attendance records to JSON file
    ${C.CYAN}--timeout=N${C.RESET}     HTTP timeout in seconds (default: 15)

  ${C.BOLD}EXAMPLES${C.RESET}
    ukuuhr connect                     # First-time: connect to device
    ukuuhr sync --once                 # One-shot sync
    ukuuhr attendance                  # Show last 7 days
    ukuuhr attendance --days=30        # Show last 30 days
    ukuuhr attendance --json --save=records.json
    ukuuhr probe                       # Discover device endpoints
    ukuuhr probe --json
    ukuuhr curl                        # Get curl commands
    ukuuhr health                      # Device health check
    ukuuhr device-info                 # Device information
    ukuuhr test /ISAPI/System/deviceInfo
    ukuuhr config                      # Show settings
  `);

  return 0;
}

// ═══════════════════════════════════════════════════════════════════════
// Time formatting helpers
// ═══════════════════════════════════════════════════════════════════════

function ts() {
  return new Date().toTimeString().substring(0, 8);
}

function formatDate(d) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

function formatTimeStr(d) {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
}

// ═══════════════════════════════════════════════════════════════════════
// Main entry point
// ═══════════════════════════════════════════════════════════════════════

async function main(args) {
  // Parse global options
  const configPath = getArgValue(args, '--config');
  const headless = args.includes('--headless') || !process.stdout.isTTY;
  const once = args.includes('--once');
  const jsonOutput = args.includes('--json');
  const timeout = parseInt(getArgValue(args, '--timeout'), 10) || 15;

  // Determine command
  const command = getCommand(args);

  // Load settings
  let settings = loadOrCreateSettings(configPath, headless);

  // Interactive setup needed
  if (!settings) {
    settings = await interactiveSetup(configPath);
  }

  if (!settings || !isValid(settings)) {
    writeErr(`No valid settings found.`);
    writeErr(`Run: ukuuhr connect  (to set up your device interactively)`);
    return 1;
  }

  // Route to command
  switch (command) {
    case 'connect':     return await cmdConnect(settings, timeout);
    case 'sync':        return await cmdSync(settings, once, timeout);
    case 'attendance':  return await cmdAttendance(settings, args, jsonOutput, timeout);
    case 'probe':       return await cmdProbe(settings, jsonOutput, timeout);
    case 'device-info': return await cmdDeviceInfo(settings, jsonOutput, timeout);
    case 'health':      return await cmdHealth(settings, jsonOutput, timeout);
    case 'curl':        return await cmdCurl(settings, timeout);
    case 'test':        return await cmdTest(settings, args, timeout);
    case 'config':      return cmdConfig(settings, configPath);
    case 'help':        return cmdHelp();
    default:            return cmdHelp();
  }
}

module.exports = { main };
