using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace UkuuHr.Sync;

/// <summary>
/// Ukuu HR Sync Bridge v2.0 — Terminal CLI
///
/// A cross-platform CLI that connects to Hikvision biometric terminals via ISAPI.
///
/// Commands:
///   sync         Fetch attendance events and push to Ukuu HR cloud (continuous or --once)
///   attendance   Pull attendance records from device and display locally
///   probe        Probe all ISAPI endpoints and report which ones the device supports
///   device-info  Show device name, model, serial, firmware, capacity
///   health       Show CPU, memory, disk usage from the device
///   curl         Generate curl commands for every ISAPI endpoint (copy-paste to terminal)
///   config       Show or edit the current settings
///   test         Test a single ISAPI endpoint by path (e.g. /ISAPI/System/deviceInfo)
///
/// Global options:
///   --config=path   Path to settings.json (default: same directory as binary)
///   --headless      Non-interactive mode (no prompts)
///   --once          For sync: single sync then exit
///   --json          Output as JSON (for probe/health/device-info/attendance)
///   --days=N        Date range in days for attendance (default: 7)
///   --save=path     Save attendance records to JSON file
///   --timeout=N     HTTP timeout in seconds (default: 15)
///
/// Examples:
///   UkuuHrSync sync --once
///   UkuuHrSync attendance
///   UkuuHrSync attendance --days=30
///   UkuuHrSync attendance --json --save=records.json
///   UkuuHrSync probe
///   UkuuHrSync probe --json
///   UkuuHrSync curl
///   UkuuHrSync health
///   UkuuHrSync device-info
///   UkuuHrSync test /ISAPI/System/deviceInfo
///   UkuuHrSync config
/// </summary>
class Program
{
    // ── ANSI Colors ──────────────────────────────────────────────────────────
    const string Reset = "\x1b[0m";
    const string Bold = "\x1b[1m";
    const string Dim = "\x1b[2m";
    const string Red = "\x1b[31m";
    const string Green = "\x1b[32m";
    const string Yellow = "\x1b[33m";
    const string Blue = "\x1b[34m";
    const string Magenta = "\x1b[35m";
    const string Cyan = "\x1b[36m";
    const string White = "\x1b[37m";
    const string BgRed = "\x1b[41m";
    const string BgGreen = "\x1b[42m";
    const string BgYellow = "\x1b[43m";

    static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        try
        {
            return await RunApp(args);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync-error.log");
            try { await File.WriteAllTextAsync(logPath, $"[{DateTime.UtcNow:O}] FATAL: {ex}\n"); } catch { }
            WriteErr($"FATAL: {ex.Message}");
            return 2;
        }
    }

    static async Task<int> RunApp(string[] args)
    {
        // ── Parse global options ────────────────────────────────────────────
        var configPath = Args.GetValue(args, "--config")
            ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
        var headless = args.Contains("--headless") || !HasTty();
        var once = args.Contains("--once");
        var jsonOutput = args.Contains("--json");
        var timeout = int.TryParse(Args.GetValue(args, "--timeout"), out var t) ? t : 15;

        // ── Determine command ───────────────────────────────────────────────
        var command = Args.GetCommand(args);

        // ── Load settings ───────────────────────────────────────────────────
        var settings = LoadOrCreateSettings(configPath, headless, command == "config");
        if (settings == null)
        {
            WriteErr($"No settings found at: {configPath}");
            WriteErr("Run: ./UkuuHrSync config  (to create settings interactively)");
            return 1;
        }

        // ── Route to command ────────────────────────────────────────────────
        return command switch
        {
            "sync"        => await CmdSync(settings, once, timeout),
            "attendance"  => await CmdAttendance(settings, args, jsonOutput, timeout),
            "probe"       => await CmdProbe(settings, jsonOutput, timeout),
            "device-info" => await CmdDeviceInfo(settings, jsonOutput, timeout),
            "health"      => await CmdHealth(settings, jsonOutput, timeout),
            "curl"        => await CmdCurl(settings, timeout),
            "config"      => CmdConfig(settings, configPath),
            "test"        => await CmdTest(settings, args, timeout),
            "help"        => CmdHelp(),
            _             => CmdHelp()
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: sync
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdSync(SyncSettings settings, bool once, int timeout)
    {
        PrintBanner("SYNC BRIDGE");

        var scheme = settings.UseHttps.GetValueOrDefault(false) ? "https" : "http";
        WriteLog($"{Cyan}  Device:   {scheme}://{settings.DeviceIp}:{settings.DevicePort}{Reset}");
        WriteLog($"{Cyan}  Username: {settings.DeviceUsername}{Reset}");
        WriteLog($"{Cyan}  Cloud:    {settings.CloudUrl}{Reset}");
        WriteLog($"{Cyan}  Interval: {settings.SyncIntervalMinutes} min{Reset}");
        WriteLog("");

        if (!once)
            WriteLog($"  Press Ctrl+C to stop. Auto-sync every {settings.SyncIntervalMinutes} min.\n");

        var lastSync = DateTime.MinValue;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await RunSync(settings, lastSync, timeout, cts.Token);
                lastSync = DateTime.UtcNow;
                WriteLog($"  {Green}[{DateTime.Now:HH:mm:ss}]{Reset} Sync complete. Next in {settings.SyncIntervalMinutes} min.\n");
            }
            catch (Exception ex)
            {
                WriteLog($"  {Red}[{DateTime.Now:HH:mm:ss}] ERROR:{Reset} {ex.Message}\n");
            }

            if (once) break;
            try { await Task.Delay(settings.SyncIntervalMinutes * 60 * 1000, cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        WriteLog("  Ukuu HR Sync Bridge stopped.");
        return 0;
    }

    static async Task RunSync(SyncSettings settings, DateTime lastSync, int timeout, CancellationToken ct)
    {
        var (baseUrl, auth) = GetConnection(settings);
        using var client = CreateHttpClient(settings, auth, timeout);

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connecting to {baseUrl}...");

        // Step 1: Get device info
        string deviceName = "Unknown", deviceModel = "Unknown";
        try
        {
            var infoResp = await client.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ct);
            if (infoResp.IsSuccessStatusCode)
            {
                var xml = await infoResp.Content.ReadAsStringAsync(ct);
                deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connected: {Bold}{deviceName}{Reset} ({deviceModel})");
            }
            else
            {
                WriteLog($"  {Yellow}[{DateTime.Now:HH:mm:ss}] WARNING:{Reset} Device info returned HTTP {(int)infoResp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  {Yellow}[{DateTime.Now:HH:mm:ss}] WARNING:{Reset} Device info failed: {ex.Message}");
        }

        // Step 2: Fetch events — 3-tier fallback (shared with attendance command)
        var fromTime = lastSync == DateTime.MinValue
            ? DateTime.UtcNow.AddDays(-7)
            : lastSync;
        var toTime = DateTime.UtcNow;

        List<ImportedPunch> events = await FetchAttendanceEvents(client, baseUrl, fromTime, toTime);

        if (events.Count == 0)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] No new events (range: {fromTime:HH:mm:ss} to {toTime:HH:mm:ss}).");
            return;
        }

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Fetched {events.Count} events. Pushing to cloud...");

        // Step 3: Push to cloud
        var payload = JsonSerializer.Serialize(new
        {
            events = events,
            deviceInfo = new { name = deviceName, model = deviceModel, serial = "" },
            faceRecognition = (object?)null
        });

        try
        {
            var cloudUrl = settings.CloudUrl!.TrimEnd('/') + "/api/attendance/save-imported";
            var request = new HttpRequestMessage(HttpMethod.Post, cloudUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(settings.ApiKey))
                request.Headers.Add("X-API-Key", settings.ApiKey);

            var cloudResp = await client.SendAsync(request, ct);
            var cloudJson = await cloudResp.Content.ReadAsStringAsync(ct);

            if (cloudResp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(cloudJson);
                    var root = doc.RootElement;
                    int fetched = root.TryGetProperty("eventsFetched", out var ef) ? ef.GetInt32() : 0;
                    int matched = root.TryGetProperty("employeesMatched", out var em) ? em.GetInt32() : 0;
                    int imported = root.TryGetProperty("recordsImported", out var ri) ? ri.GetInt32() : 0;
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud: {fetched} fetched, {matched} matched, {imported} imported.");
                }
                catch
                {
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud OK (response: {Truncate(cloudJson, 200)})");
                }
            }
            else
            {
                WriteLog($"  {Red}[{DateTime.Now:HH:mm:ss}] Cloud error HTTP {(int)cloudResp.StatusCode}:{Reset} {Truncate(cloudJson, 200)}");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  {Red}[{DateTime.Now:HH:mm:ss}] Cloud push failed:{Reset} {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: attendance — pull attendance records from device
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdAttendance(SyncSettings settings, string[] args, bool jsonOutput, int timeout)
    {
        PrintBanner("ATTENDANCE RECORDS");

        var days = int.TryParse(Args.GetValue(args, "--days"), out var d) ? d : 7;
        var savePath = Args.GetValue(args, "--save");
        var fromTime = DateTime.UtcNow.AddDays(-days);
        var toTime = DateTime.UtcNow;

        var (baseUrl, auth) = GetConnection(settings);
        using var client = CreateHttpClient(settings, auth, timeout);

        WriteLog($"  Fetching attendance records from {Bold}{baseUrl}{Reset}");
        WriteLog($"  Date range: {fromTime:yyyy-MM-dd HH:mm} to {toTime:yyyy-MM-dd HH:mm} ({days} days)\n");

        // Get device info
        string deviceName = "Unknown", deviceModel = "Unknown";
        try
        {
            var infoResp = await client.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo");
            if (infoResp.IsSuccessStatusCode)
            {
                var xml = await infoResp.Content.ReadAsStringAsync(ct: default);
                deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                WriteLog($"  Device: {Bold}{deviceName}{Reset} ({deviceModel})\n");
            }
        }
        catch { }

        // Fetch events using 3-tier fallback
        List<ImportedPunch> events = await FetchAttendanceEvents(client, baseUrl, fromTime, toTime);

        if (events.Count == 0)
        {
            WriteLog($"  {Yellow}No attendance records found.{Reset}");
            WriteLog($"  {Dim}Try increasing the date range: UkuuHrSync attendance --days=30{Reset}");
            return 0;
        }

        // JSON output
        if (jsonOutput)
        {
            var json = JsonSerializer.Serialize(new
            {
                device = new { name = deviceName, model = deviceModel, ip = settings.DeviceIp },
                range = new { from = fromTime, to = toTime, days },
                totalEvents = events.Count,
                events = events
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);

            if (!string.IsNullOrEmpty(savePath))
            {
                await File.WriteAllTextAsync(savePath, json);
                WriteLog($"  {Green}Saved to: {savePath}{Reset}");
            }
            return 0;
        }

        // ── Display attendance table ─────────────────────────────────────────
        WriteLog($"  {Bold}ATTENDANCE RECORDS{Reset}  ({events.Count} total)\n");

        // Group by date
        var byDate = events
            .GroupBy(e => DateTime.TryParse(e.Time, out var t) ? t.ToString("yyyy-MM-dd") : "unknown")
            .OrderByDescending(g => g.Key);

        foreach (var dateGroup in byDate)
        {
            var dateEvents = dateGroup.OrderBy(e => e.Time).ToList();
            var checkIns = dateEvents.Count(e => e.EventType == "check_in");
            var checkOuts = dateEvents.Count(e => e.EventType == "check_out");

            WriteLog($"  {Magenta}{Bold}{dateGroup.Key}{Reset}  " +
                $"{Green}{checkIns} check-ins{Reset}  " +
                $"{Cyan}{checkOuts} check-outs{Reset}  " +
                $"{Dim}{dateEvents.Count} total{Reset}");
            WriteLog($"  {new string('─', 70)}");

            WriteLog($"  {Dim}{"Employee",-12}{Reset} {Dim}{"Time",-10}{Reset} {Dim}{"Type",-12}{Reset} {Dim}{"Minor"}{Reset}");

            foreach (var e in dateEvents)
            {
                var timeOnly = DateTime.TryParse(e.Time, out var parsedTime)
                    ? parsedTime.ToString("HH:mm:ss") : e.Time?[Math.Max(0, e.Time.Length - 8)..] ?? "?";
                var typeColor = e.EventType == "check_in" ? Green : Cyan;
                var typeLabel = e.EventType == "check_in" ? "CHECK IN" : "CHECK OUT";

                WriteLog($"  {e.EmployeeNo,-12} {timeOnly,-10} {typeColor}{typeLabel,-12}{Reset} {Dim}{e.Minor}{Reset}");
            }
            WriteLog("");
        }

        // ── Employee summary ──────────────────────────────────────────────────
        WriteLog($"  {Bold}EMPLOYEE SUMMARY{Reset}");
        WriteLog($"  {new string('─', 50)}");

        var byEmployee = events
            .GroupBy(e => e.EmployeeNo)
            .OrderByDescending(g => g.Count())
            .ToList();

        WriteLog($"  {Dim}{"Employee",-12}{Reset} {Dim}{"Total",-8}{Reset} {Dim}{"Check-ins",-12}{Reset} {Dim}{"Check-outs"}{Reset}");
        foreach (var emp in byEmployee)
        {
            var ins = emp.Count(e => e.EventType == "check_in");
            var outs = emp.Count(e => e.EventType == "check_out");
            WriteLog($"  {emp.Key,-12} {emp.Count(),-8} {Green}{ins,-12}{Reset} {Cyan}{outs}{Reset}");
        }
        WriteLog($"  {new string('─', 50)}");
        WriteLog($"  {Bold}{byEmployee.Count} unique employees{Reset}, {events.Count} total records");

        // ── Save to file if requested ─────────────────────────────────────────
        if (!string.IsNullOrEmpty(savePath))
        {
            var json = JsonSerializer.Serialize(new
            {
                device = new { name = deviceName, model = deviceModel, ip = settings.DeviceIp },
                range = new { from = fromTime, to = toTime, days },
                totalEvents = events.Count,
                events = events
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(savePath, json);
            WriteLog($"\n  {Green}Saved to: {savePath}{Reset}");
        }

        return 0;
    }

    /// <summary>
    /// Fetch attendance events using the 3-tier fallback:
    /// AcsEvent JSON → AcsEvent XML → AuditLog XML
    /// </summary>
    static async Task<List<ImportedPunch>> FetchAttendanceEvents(HttpClient client, string baseUrl, DateTime fromTime, DateTime toTime)
    {
        List<ImportedPunch> events = new();
        string? tier1Error = null, tier2Error = null, tier3Error = null;

        // Tier 1: AcsEvent JSON (?format=json)
        try
        {
            var searchXml = BuildAcsEventSearchXml(fromTime, toTime);
            var content = new StringContent(searchXml, Encoding.UTF8, "application/xml");
            var resp = await client.PostAsync($"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json", content);

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct: default);
                events = body.TrimStart().StartsWith("{") || body.TrimStart().StartsWith("[")
                    ? HikvisionParser.ParseAcsEventJson(body)
                    : HikvisionParser.ParseAcsEventXml(body);
                WriteLog($"  {Green}AcsEvent (JSON){Reset}: {events.Count} records");
            }
            else
            {
                var errBody = await ReadErrorBody(resp);
                tier1Error = $"HTTP {(int)resp.StatusCode} — {errBody}";
                WriteLog($"  {Yellow}AcsEvent (JSON){Reset}: HTTP {(int)resp.StatusCode} — {errBody}");
            }
        }
        catch (Exception ex) { tier1Error = ex.Message; }

        // Tier 2: AcsEvent XML (no ?format=json)
        if (events.Count == 0 && tier1Error != null)
        {
            try
            {
                var searchXml = BuildAcsEventSearchXml(fromTime, toTime);
                var content = new StringContent(searchXml, Encoding.UTF8, "application/xml");
                var resp = await client.PostAsync($"{baseUrl}/ISAPI/AccessControl/AcsEvent", content);

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct: default);
                    events = HikvisionParser.ParseAcsEventXml(body);
                    WriteLog($"  {Green}AcsEvent (XML){Reset}: {events.Count} records");
                }
                else
                {
                    var errBody = await ReadErrorBody(resp);
                    tier2Error = $"HTTP {(int)resp.StatusCode} — {errBody}";
                    WriteLog($"  {Yellow}AcsEvent (XML){Reset}: HTTP {(int)resp.StatusCode}");
                }
            }
            catch (Exception ex) { tier2Error = ex.Message; }
        }

        // Tier 3: AuditLog
        if (events.Count == 0 && tier2Error != null)
        {
            try
            {
                var s = fromTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var e = toTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var auditUrl = $"{baseUrl}/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime={Uri.EscapeDataString(s)}&endTime={Uri.EscapeDataString(e)}";
                var resp = await client.GetAsync(auditUrl);

                if (resp.IsSuccessStatusCode)
                {
                    var xml = await resp.Content.ReadAsStringAsync(ct: default);
                    events = HikvisionParser.ParseAuditLogXml(xml);
                    WriteLog($"  {Green}AuditLog{Reset}: {events.Count} records");
                }
                else
                {
                    var errBody = await ReadErrorBody(resp);
                    tier3Error = $"HTTP {(int)resp.StatusCode} — {errBody}";
                    WriteLog($"  {Red}AuditLog{Reset}: HTTP {(int)resp.StatusCode}");
                }
            }
            catch (Exception ex) { tier3Error = ex.Message; }
        }

        // Error summary
        if (events.Count == 0 && tier1Error != null && tier2Error != null && tier3Error != null)
        {
            WriteLog($"\n  {Red}All event endpoints failed:{Reset}");
            WriteLog($"    AcsEvent JSON: {tier1Error}");
            WriteLog($"    AcsEvent XML:  {tier2Error}");
            WriteLog($"    AuditLog:      {tier3Error}");
            WriteLog($"  {Cyan}Tip: Run 'UkuuHrSync probe' to discover which endpoints your device supports.{Reset}");
        }

        return events;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: probe
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdProbe(SyncSettings settings, bool jsonOutput, int timeout)
    {
        PrintBanner("ISAPI ENDPOINT PROBE");

        var (baseUrl, auth) = GetConnection(settings);
        using var client = CreateHttpClient(settings, auth, timeout);

        WriteLog($"  Probing {Bold}{baseUrl}{Reset} ...\n");

        var probes = DefineProbes();
        var results = new List<ProbeResult>();

        foreach (var probe in probes)
        {
            WriteLog($"  {Dim}Testing{Reset} {probe.Name} ({probe.Method} {probe.Path})...");
            var result = await RunProbe(client, baseUrl, settings, probe);
            results.Add(result);

            var statusColor = result.IsSuccess ? Green : result.IsUnsupported ? Yellow : Red;
            var statusIcon = result.IsSuccess ? "OK" : result.StatusCode > 0 ? $"{result.StatusCode}" : "FAIL";
            WriteLog($"    {statusColor}{Bold}{statusIcon}{Reset}  {result.ElapsedMs}ms  {result.Name}");
        }

        WriteLog("");

        // Summary table
        var ok = results.Count(r => r.IsSuccess);
        var fail = results.Count(r => r.IsFailed);
        var unsup = results.Count(r => r.IsUnsupported);

        if (jsonOutput)
        {
            var json = JsonSerializer.Serialize(new
            {
                baseUrl = baseUrl,
                probedAt = DateTime.UtcNow,
                total = results.Count,
                ok = ok,
                failed = fail,
                unsupported = unsup,
                probes = results.Select(r => new
                {
                    r.Name, r.Category, r.Method, r.Path,
                    r.StatusCode, r.ElapsedMs,
                    success = r.IsSuccess,
                    error = r.ErrorMessage,
                    responsePreview = Truncate(r.ResponseBody, 500)
                })
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            return 0;
        }

        // Print full table
        WriteLog($"  {Bold}PROBE RESULTS{Reset}");
        WriteLog($"  {new string('─', 80)}");

        var currentCat = "";
        foreach (var r in results)
        {
            if (r.Category != currentCat)
            {
                currentCat = r.Category;
                WriteLog($"  {Magenta}{Bold}{currentCat}{Reset}");
            }

            var statusColor = r.IsSuccess ? Green : r.IsUnsupported ? Yellow : Red;
            var icon = r.IsSuccess ? "OK" : r.StatusCode > 0 ? $"{r.StatusCode}" : "FAIL";
            WriteLog($"    {statusColor}{icon,5}{Reset}  {r.ElapsedMs,5}ms  {Cyan}{r.Method,-5}{Reset}  {r.Name}");
            WriteLog($"    {new string(' ', 14)}{Dim}{r.Path}{Reset}");

            if (!r.IsSuccess && !string.IsNullOrEmpty(r.ErrorMessage))
                WriteLog($"    {new string(' ', 14)}{Red}{Truncate(r.ErrorMessage, 80)}{Reset}");
        }

        WriteLog($"  {new string('─', 80)}");
        WriteLog($"  {Green}{ok} OK{Reset}  {Red}{fail} Failed{Reset}  {Yellow}{unsup} Unsupported{Reset}  / {results.Count} total");
        WriteLog("");

        // Recommendation
        var acsJson = results.FirstOrDefault(r => r.Name == "AcsEvent (JSON)");
        var acsXml = results.FirstOrDefault(r => r.Name == "AcsEvent (XML)");
        var auditLog = results.FirstOrDefault(r => r.Name == "AuditLog Search");

        WriteLog($"  {Bold}RECOMMENDATION{Reset}");
        if (acsJson?.IsSuccess == true)
            WriteLog($"    {Green}Your device supports AcsEvent with ?format=json — this is the preferred endpoint.{Reset}");
        else if (acsXml?.IsSuccess == true)
            WriteLog($"    {Yellow}Your device does NOT support ?format=json, but AcsEvent XML works.{Reset}");
        else if (auditLog?.IsSuccess == true)
            WriteLog($"    {Yellow}AcsEvent is not supported. Use AuditLog as the event source.{Reset}");
        else
            WriteLog($"    {Red}No event endpoints are working. Check credentials and network.{Reset}");

        WriteLog($"\n  Run {Cyan}UkuuHrSync curl{Reset} to get terminal commands for manual testing.");

        return fail == results.Count ? 1 : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: device-info
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdDeviceInfo(SyncSettings settings, bool jsonOutput, int timeout)
    {
        PrintBanner("DEVICE INFO");

        var (baseUrl, auth) = GetConnection(settings);
        using var client = CreateHttpClient(settings, auth, timeout);

        try
        {
            var resp = await client.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo");
            if (!resp.IsSuccessStatusCode)
            {
                WriteErr($"Failed: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            var xml = await resp.Content.ReadAsStringAsync(ct: default);

            if (jsonOutput)
            {
                Console.WriteLine(xml);
                return 0;
            }

            // Parse and display key fields
            var fields = new[] { "deviceName", "deviceID", "model", "serialNumber", "macAddress",
                "firmwareVersion", "hardwareVersion", "deviceType", "maxUsers", "maxFingers", "maxFaces", "maxCards" };

            WriteLog($"  {Bold}DEVICE INFORMATION{Reset}");
            WriteLog($"  {new string('─', 50)}");
            foreach (var field in fields)
            {
                var value = HikvisionParser.ExtractXmlValue(xml, field);
                if (value != null)
                {
                    var label = char.ToUpper(field[0]) + field[1..];
                    WriteLog($"  {Cyan}{label,-18}{Reset} {value}");
                }
            }
            WriteLog($"  {new string('─', 50)}");

            // Try capabilities
            try
            {
                var capResp = await client.GetAsync($"{baseUrl}/ISAPI/System/capabilities");
                if (capResp.IsSuccessStatusCode)
                {
                    var capXml = await capResp.Content.ReadAsStringAsync(ct: default);
                    WriteLog($"\n  {Bold}CAPABILITIES{Reset} (raw XML available with --json)");
                    WriteLog($"  {Dim}{Truncate(capXml, 500)}{Reset}");
                }
            }
            catch { }

            return 0;
        }
        catch (Exception ex)
        {
            WriteErr($"Connection failed: {ex.Message}");
            return 1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: health
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdHealth(SyncSettings settings, bool jsonOutput, int timeout)
    {
        PrintBanner("DEVICE HEALTH");

        var (baseUrl, auth) = GetConnection(settings);
        using var client = CreateHttpClient(settings, auth, timeout);

        try
        {
            var resp = await client.GetAsync($"{baseUrl}/ISAPI/System/status?format=json");
            if (!resp.IsSuccessStatusCode)
            {
                // Fallback to XML
                resp = await client.GetAsync($"{baseUrl}/ISAPI/System/status");
            }

            if (!resp.IsSuccessStatusCode)
            {
                WriteErr($"Failed: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            var body = await resp.Content.ReadAsStringAsync(ct: default);

            if (jsonOutput)
            {
                Console.WriteLine(body);
                return 0;
            }

            // Parse JSON health
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("DeviceStatus", out var status))
                {
                    WriteLog($"  {Bold}DEVICE HEALTH{Reset}");
                    WriteLog($"  {new string('─', 40)}");

                    if (status.TryGetProperty("currentCpuUsage", out var cpu))
                        PrintBar("CPU", cpu.TryGetDouble(out var c) ? c : 0);
                    if (status.TryGetProperty("currentMemoryUsage", out var mem))
                        PrintBar("Memory", mem.TryGetDouble(out var m) ? m : 0);
                    if (status.TryGetProperty("currentDiskUsage", out var disk))
                        PrintBar("Disk", disk.TryGetDouble(out var d) ? d : 0);
                    if (status.TryGetProperty("upTime", out var uptime))
                        WriteLog($"  {Cyan}Uptime{Reset}           {uptime.GetInt32()}s ({uptime.GetInt32() / 3600}h {(uptime.GetInt32() % 3600) / 60}m)");

                    WriteLog($"  {new string('─', 40)}");
                }
                else
                {
                    WriteLog($"  {Dim}{Truncate(body, 500)}{Reset}");
                }
            }
            catch
            {
                WriteLog($"  {Dim}{Truncate(body, 500)}{Reset}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            WriteErr($"Connection failed: {ex.Message}");
            return 1;
        }
    }

    static void PrintBar(string label, double pct)
    {
        var barLen = 20;
        var filled = (int)Math.Round(pct / 100 * barLen);
        var empty = barLen - filled;
        var color = pct < 70 ? Green : pct < 90 ? Yellow : Red;
        var bar = new string('█', filled) + new string('░', empty);
        WriteLog($"  {Cyan}{label,-10}{Reset} {color}{bar}{Reset} {pct,5:F1}%");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: curl
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdCurl(SyncSettings settings, int timeout)
    {
        PrintBanner("CURL COMMANDS");

        var (baseUrl, _) = GetConnection(settings);
        var probes = DefineProbes();

        WriteLog($"  # ISAPI Endpoint Commands for {settings.DeviceIp}");
        WriteLog($"  # Device: {baseUrl}");
        WriteLog($"  # Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        WriteLog("");

        foreach (var probe in probes)
        {
            var curl = GenerateCurlCommand(baseUrl, settings, probe);
            WriteLog($"  # {probe.Name} ({probe.Method} {probe.Path})");
            WriteLog($"  {curl}");
            WriteLog("");
        }

        WriteLog($"  {Cyan}Tip:{Reset} Run these from any terminal on the same network.");
        WriteLog($"  {Cyan}Tip:{Reset} Successful commands (HTTP 200) confirm device support.");

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: test
    // ═══════════════════════════════════════════════════════════════════════
    static async Task<int> CmdTest(SyncSettings settings, string[] args, int timeout)
    {
        var path = args.SkipWhile(a => a != "test").Skip(1).FirstOrDefault()
            ?? "/ISAPI/System/deviceInfo";

        PrintBanner("ENDPOINT TEST");

        var (baseUrl, auth) = GetConnection(settings);
        using var client = CreateHttpClient(settings, auth, timeout);
        var fullUrl = $"{baseUrl}{path}";

        WriteLog($"  Testing: {Bold}{fullUrl}{Reset}\n");

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await client.GetAsync(fullUrl);
            sw.Stop();
            var body = await resp.Content.ReadAsStringAsync(ct: default);

            var statusColor = resp.IsSuccessStatusCode ? Green : Red;
            WriteLog($"  {statusColor}{Bold}HTTP {(int)resp.StatusCode}{Reset} {resp.ReasonPhrase}  ({sw.ElapsedMilliseconds}ms)");
            WriteLog($"  {new string('─', 60)}");
            WriteLog($"  {Dim}{Truncate(body, 1000)}{Reset}");

            WriteLog($"\n  {Bold}Equivalent curl:{Reset}");
            WriteLog($"  curl --digest -u '{settings.DeviceUsername}:{settings.DevicePassword}' \\\n    -H 'Accept: application/xml, application/json' \\\n    '{fullUrl}'");

            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            sw.Stop();
            WriteLog($"  {Red}{Bold}FAILED{Reset} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            return 1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: config
    // ═══════════════════════════════════════════════════════════════════════
    static int CmdConfig(SyncSettings settings, string configPath)
    {
        PrintBanner("CONFIGURATION");

        WriteLog($"  {Bold}Settings file:{Reset} {configPath}");
        WriteLog($"  {new string('─', 50)}");
        WriteLog($"  {Cyan}Device IP:{Reset}       {settings.DeviceIp}");
        WriteLog($"  {Cyan}Port:{Reset}            {settings.DevicePort}");
        WriteLog($"  {Cyan}HTTPS:{Reset}           {settings.UseHttps}");
        WriteLog($"  {Cyan}Username:{Reset}        {settings.DeviceUsername}");
        WriteLog($"  {Cyan}Password:{Reset}        {new string('*', Math.Min(settings.DevicePassword?.Length ?? 0, 20))}");
        WriteLog($"  {Cyan}Cloud URL:{Reset}       {settings.CloudUrl}");
        WriteLog($"  {Cyan}API Key:{Reset}         {(string.IsNullOrEmpty(settings.ApiKey) ? "(not set)" : new string('*', Math.Min(settings.ApiKey.Length, 20)))}");
        WriteLog($"  {Cyan}Sync Interval:{Reset}   {settings.SyncIntervalMinutes} min");
        WriteLog($"  {new string('─', 50)}");
        WriteLog($"\n  Edit {configPath} to change settings.");

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMAND: help
    // ═══════════════════════════════════════════════════════════════════════
    static int CmdHelp()
    {
        PrintBanner("HELP");

        WriteLog($"""
          {Bold}USAGE{Reset}
            UkuuHrSync <command> [options]

          {Bold}COMMANDS{Reset}
            {Cyan}sync{Reset}         Fetch attendance events and push to cloud (continuous or --once)
            {Cyan}attendance{Reset}   Pull attendance records from device and display locally
            {Cyan}probe{Reset}        Probe all ISAPI endpoints — discover what your device supports
            {Cyan}device-info{Reset}  Show device name, model, serial, firmware, capacity
            {Cyan}health{Reset}       Show CPU, memory, disk usage from the device
            {Cyan}curl{Reset}         Generate curl commands for every ISAPI endpoint
            {Cyan}test{Reset} <path>  Test a single ISAPI endpoint by path
            {Cyan}config{Reset}       Show current settings and config file location
            {Cyan}help{Reset}         Show this help message

          {Bold}OPTIONS{Reset}
            {Cyan}--config=path{Reset}   Path to settings.json
            {Cyan}--headless{Reset}      Non-interactive mode
            {Cyan}--once{Reset}          For sync: single sync then exit
            {Cyan}--json{Reset}          JSON output (probe/health/device-info/attendance)
            {Cyan}--days=N{Reset}       Date range in days for attendance (default: 7)
            {Cyan}--save=path{Reset}    Save attendance records to JSON file
            {Cyan}--timeout=N{Reset}     HTTP timeout in seconds (default: 15)

          {Bold}EXAMPLES{Reset}
            UkuuHrSync sync --once
            UkuuHrSync attendance
            UkuuHrSync attendance --days=30
            UkuuHrSync attendance --json --save=records.json
            UkuuHrSync probe
            UkuuHrSync probe --json
            UkuuHrSync curl
            UkuuHrSync health
            UkuuHrSync device-info
            UkuuHrSync test /ISAPI/System/deviceInfo
            UkuuHrSync config
        """);

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Probe Engine
    // ═══════════════════════════════════════════════════════════════════════

    record ProbeDef(string Category, string Name, string Method, string Path, string? PostBody = null);
    record ProbeResult(string Name, string Category, string Method, string Path, string FullUrl,
        int StatusCode, string? ErrorMessage, string ResponseBody, long ElapsedMs, string CurlCommand)
    {
        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
        public bool IsFailed => StatusCode >= 400 || !string.IsNullOrEmpty(ErrorMessage);
        public bool IsUnsupported => StatusCode == 404 || StatusCode == 400;
    }

    static List<ProbeDef> DefineProbes()
    {
        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-1);
        var searchXml = BuildAcsEventSearchXml(yesterday, now);
        var fromStr = Uri.EscapeDataString(yesterday.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var toStr = Uri.EscapeDataString(now.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return new List<ProbeDef>
        {
            // System
            new("System", "Device Info", "GET", "/ISAPI/System/deviceInfo"),
            new("System", "Capabilities", "GET", "/ISAPI/System/capabilities"),
            new("System", "Device Status (JSON)", "GET", "/ISAPI/System/status?format=json"),
            new("System", "Device Status (XML)", "GET", "/ISAPI/System/status"),
            new("System", "Device Time", "GET", "/ISAPI/System/time"),
            new("System", "Network Config", "GET", "/ISAPI/System/networkInterfaces"),
            new("System", "Device Capacity", "GET", "/ISAPI/System/deviceCapacity"),

            // Access Control
            new("Access Control", "AcsEvent (JSON)", "POST", "/ISAPI/AccessControl/AcsEvent?format=json", searchXml),
            new("Access Control", "AcsEvent (XML)", "POST", "/ISAPI/AccessControl/AcsEvent", searchXml),
            new("Access Control", "AuditLog Search", "GET", $"/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime={fromStr}&endTime={toStr}"),
            new("Access Control", "AuditLog (no params)", "GET", "/ISAPI/AccessControl/AuditLog/search"),
            new("Access Control", "Door Status", "GET", "/ISAPI/AccessControl/Door/status"),

            // People
            new("People", "All Persons", "GET", "/ISAPI/AccessControl/UserInfo/Search?format=json"),

            // Events
            new("Events", "Event Notification Caps", "GET", "/ISAPI/Event/notification/capabilities"),

            // Security
            new("Security", "Security Caps", "GET", "/ISAPI/Security/capabilities"),
        };
    }

    static async Task<ProbeResult> RunProbe(HttpClient client, string baseUrl, SyncSettings settings, ProbeDef probe)
    {
        var fullUrl = $"{baseUrl}{probe.Path}";
        var sw = Stopwatch.StartNew();
        int statusCode = 0;
        string? errorMsg = null;
        string body = "";

        try
        {
            HttpResponseMessage? resp = null;
            if (probe.Method == "GET")
            {
                resp = await client.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead);
            }
            else if (probe.Method == "POST" && probe.PostBody != null)
            {
                using var content = new StringContent(probe.PostBody, Encoding.UTF8, "application/xml");
                resp = await client.PostAsync(fullUrl, content);
            }

            if (resp != null)
            {
                statusCode = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(ct: default);
                body = Truncate(body, 2000);
                resp.Dispose();
            }
        }
        catch (TaskCanceledException) { errorMsg = "Request timed out"; statusCode = 0; }
        catch (HttpRequestException ex) { errorMsg = ex.Message; statusCode = (int?)ex.StatusCode ?? 0; }
        catch (Exception ex) { errorMsg = ex.Message; statusCode = 0; }

        sw.Stop();
        var curl = GenerateCurlCommand(baseUrl, settings, probe);

        return new ProbeResult(probe.Name, probe.Category, probe.Method, probe.Path,
            fullUrl, statusCode, errorMsg, body, sw.ElapsedMilliseconds, curl);
    }

    static string GenerateCurlCommand(string baseUrl, SyncSettings settings, ProbeDef probe)
    {
        var sb = new StringBuilder();
        sb.Append("curl -v");
        sb.Append($" --digest -u '{settings.DeviceUsername}:{settings.DevicePassword}'");

        if (probe.Method != "GET")
            sb.Append($" -X {probe.Method}");

        sb.Append(" -H 'Accept: application/xml, application/json'");

        if (!string.IsNullOrEmpty(probe.PostBody))
        {
            var escaped = probe.PostBody.Replace("'", "'\\''");
            sb.Append(" -H 'Content-Type: application/xml'");
            sb.Append($" -d '{escaped}'");
        }

        sb.Append($" '{baseUrl}{probe.Path}'");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HTTP Helpers
    // ═══════════════════════════════════════════════════════════════════════

    static (string baseUrl, string auth) GetConnection(SyncSettings settings)
    {
        var scheme = settings.UseHttps.GetValueOrDefault(false) ? "https" : "http";
        var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.DeviceUsername}:{settings.DevicePassword}"));
        return (baseUrl, auth);
    }

    static HttpClient CreateHttpClient(SyncSettings settings, string auth, int timeout)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            PreAuthenticate = true,
            Credentials = new System.Net.NetworkCredential(settings.DeviceUsername, settings.DevicePassword)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeout)
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    static async Task<string> ReadErrorBody(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct: default);
            return Truncate(body, 300);
        }
        catch { return ""; }
    }

    static string BuildAcsEventSearchXml(DateTime from, DateTime to)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEventSearchDescription>
    <searchID>probe_test</searchID>
    <searchResultPosition>0</searchResultPosition>
    <maxResults>5</maxResults>
    <major>1</major>
    <minor>0</minor>
    <startTime>{from:yyyy-MM-ddTHH:mm:ssZ}</startTime>
    <endTime>{to:yyyy-MM-ddTHH:mm:ssZ}</endTime>
</AcsEventSearchDescription>";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Settings
    // ═══════════════════════════════════════════════════════════════════════

    static SyncSettings? LoadOrCreateSettings(string path, bool headlessMode, bool isConfigCommand)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<SyncSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null && loaded.IsValid())
                {
                    WriteLog($"{Dim}  Loaded: {path}{Reset}");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"  WARNING: Could not load settings.json: {ex.Message}");
            }
        }

        // Headless / no TTY: create defaults
        if (headlessMode || !HasTty())
        {
            var defaults = new SyncSettings();
            try
            {
                var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(path, json);
                WriteLog($"  Created default settings at: {path}");
            }
            catch { }
            return defaults;
        }

        // Interactive setup
        Console.WriteLine("  First-time setup — enter your Hikvision device details:\n");

        Console.Write("  Device IP Address [192.168.1.137]: ");
        var ip = ReadLineSafe()?.Trim();
        if (string.IsNullOrEmpty(ip)) ip = "192.168.1.137";

        Console.Write("  Port [80]: ");
        var portStr = ReadLineSafe()?.Trim();
        int port = int.TryParse(portStr, out var p) ? p : 80;

        Console.Write("  Use HTTPS? (y/n) [n]: ");
        var https = ReadLineSafe()?.Trim().ToLower() == "y";

        Console.Write("  Username [admin]: ");
        var user = ReadLineSafe()?.Trim();
        if (string.IsNullOrEmpty(user)) user = "admin";

        Console.Write("  Password: ");
        var pass = ReadPassword();

        Console.Write("  Cloud URL [https://ukuuhr.com]: ");
        var cloudUrl = ReadLineSafe()?.Trim();
        if (string.IsNullOrEmpty(cloudUrl)) cloudUrl = "https://ukuuhr.com";

        Console.Write("  API Key (leave empty if not set): ");
        var apiKey = ReadLineSafe()?.Trim();

        Console.Write("  Sync interval in minutes [5]: ");
        var intervalStr = ReadLineSafe()?.Trim();
        int interval = int.TryParse(intervalStr, out var i) ? i : 5;

        var settings = new SyncSettings
        {
            DeviceIp = ip,
            DevicePort = port,
            UseHttps = https,
            DeviceUsername = user,
            DevicePassword = pass,
            CloudUrl = cloudUrl,
            ApiKey = apiKey,
            SyncIntervalMinutes = interval
        };

        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(path, json);
            Console.WriteLine($"\n  Settings saved to: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"\n  WARNING: Could not save settings: {ex.Message}"); }

        return settings;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Terminal Helpers
    // ═══════════════════════════════════════════════════════════════════════

    static bool HasTty()
    {
        try { return !Console.IsInputRedirected; }
        catch { return false; }
    }

    static void WriteLog(string message)
    {
        try { Console.WriteLine(message); } catch { }
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync.log");
            // Strip ANSI codes for log file
            var clean = System.Text.RegularExpressions.Regex.Replace(message, @"\x1b\[[0-9;]*m", "");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {clean}\n");
        }
        catch { }
    }

    static void WriteErr(string message)
    {
        try { Console.Error.WriteLine($"{Red}{message}{Reset}"); } catch { }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    static void PrintBanner(string subtitle)
    {
        var lines = new[]
        {
            "",
            $"  {Magenta}{Bold}╔═══════════════════════════════════════════════╗{Reset}",
            $"  {Magenta}{Bold}║{Reset}     {White}{Bold}UKUU HR — SYNC BRIDGE v2.0{Reset}          {Magenta}{Bold}║{Reset}",
            $"  {Magenta}{Bold}╠═══════════════════════════════════════════════╣{Reset}",
            $"  {Magenta}{Bold}║{Reset}  {Cyan}{subtitle}{Reset}                {Magenta}{Bold}║{Reset}",
            $"  {Magenta}{Bold}╚═══════════════════════════════════════════════╝{Reset}",
            ""
        };
        foreach (var line in lines)
        {
            try { Console.WriteLine(line); } catch { }
        }
    }

    static string? ReadLineSafe()
    {
        try { return Console.ReadLine(); }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    static string ReadPassword()
    {
        var pass = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(true); }
            catch (IOException) { Console.WriteLine(); return ""; }
            catch (InvalidOperationException) { Console.WriteLine(); return ""; }

            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
            {
                pass.Remove(pass.Length - 1, 1);
                try { Console.Write("\b \b"); } catch { }
            }
            else if (key.KeyChar != '\0')
            {
                pass.Append(key.KeyChar);
                try { Console.Write("*"); } catch { }
            }
        }
        Console.WriteLine();
        return pass.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Argument Parsing
// ═══════════════════════════════════════════════════════════════════════

static class Args
{
    public static string? GetCommand(string[] args)
    {
        var commands = new[] { "sync", "attendance", "probe", "device-info", "health", "curl", "test", "config", "help" };
        foreach (var arg in args)
        {
            if (commands.Contains(arg)) return arg;
        }
        return "sync"; // default command
    }

    public static string? GetValue(string[] args, string prefix)
    {
        var match = args.FirstOrDefault(a => a.StartsWith($"{prefix}="));
        return match?[(prefix.Length + 1)..];
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Data models
// ═══════════════════════════════════════════════════════════════════════

class SyncSettings
{
    public string DeviceIp { get; set; } = "192.168.1.137";
    public int DevicePort { get; set; } = 80;
    public bool? UseHttps { get; set; } = false;
    public string DeviceUsername { get; set; } = "admin";
    public string DevicePassword { get; set; } = "";
    public string CloudUrl { get; set; } = "https://ukuuhr.com";
    public string? ApiKey { get; set; }
    public int SyncIntervalMinutes { get; set; } = 5;

    public bool IsValid() =>
        !string.IsNullOrEmpty(DeviceIp) &&
        !string.IsNullOrEmpty(DeviceUsername) &&
        !string.IsNullOrEmpty(CloudUrl);
}
