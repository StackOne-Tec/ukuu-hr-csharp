using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UkuuHr.Sync;

/// <summary>
/// Ukuu HR Access Sync Bridge v2.4.0
/// 
/// A cross-platform desktop application that:
/// 1. Connects to a Hikvision biometric/access terminal via ISAPI (local network)
/// 2. Fetches access events (door/terminal access records — universal for all device types)
/// 3. Pushes them to the Ukuu HR cloud API (ukuuhr.com)
/// 4. Auto-syncs on a configurable interval (default: every 5 minutes)
/// 
/// ISAPI 3-Tier Fallback:
///   Tier 1: AcsEvent JSON  — POST /ISAPI/AccessControl/AcsEvent?format=json
///   Tier 2: AcsEvent XML   — POST /ISAPI/AccessControl/AcsEvent (XML body)
///   Tier 3: AuditLog XML   — POST /ISAPI/System/AuditLog (legacy devices only)
/// 
/// Auth: HTTP Digest (required by Hikvision firmware V4.x+)
/// </summary>
class Program
{
    // .NET's HttpClientHandler with Credentials automatically handles HTTP Digest
    // auth (RFC 2617, qop=auth, MD5) when the device returns 401 + WWW-Authenticate: Digest.
    // PreAuthenticate=true sends credentials on the first request to avoid extra round-trips.
    private static HttpClient _httpClient = CreateDigestClient("admin", "");

    private static HttpClient CreateDigestClient(string username, string password)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static bool HasTty()
    {
        try { return !Console.IsInputRedirected; }
        catch { return false; }
    }

    static async Task<int> Main(string[] args)
    {
        try { return await RunApp(args); }
        catch (Exception ex)
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync-error.log");
            try { await File.WriteAllTextAsync(logPath, $"[{DateTime.UtcNow:O}] FATAL: {ex}\n"); } catch { }
            try { Console.Error.WriteLine($"FATAL: {ex.Message}"); } catch { }
            return 2;
        }
    }

    static async Task<int> RunApp(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        bool onceMode = args.Contains("--once");
        bool headlessMode = args.Contains("--headless");
        string? configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?["--config=".Length..];
        configPath ??= Path.Combine(AppContext.BaseDirectory, "settings.json");

        if (!headlessMode && !HasTty())
        {
            headlessMode = true;
            WriteLog("Running in headless mode (no TTY detected).");
        }

        PrintBanner();

        var settings = LoadOrCreateSettings(configPath, headlessMode);
        if (settings == null)
        {
            WriteLog($"No settings found at: {configPath}");
            WriteLog("Run from Terminal with: ./UkuuHrSync");
            return 1;
        }

        // Create HttpClient with Digest auth for this device
        _httpClient = CreateDigestClient(settings.DeviceUsername, settings.DevicePassword);

        var scheme = settings.UseHttps.GetValueOrDefault(false) ? "https" : "http";
        WriteLog($"\n  Device:   {scheme}://{settings.DeviceIp}:{settings.DevicePort}");
        WriteLog($"  Username: {settings.DeviceUsername}");
        WriteLog($"  Cloud:    {settings.CloudUrl}");
        WriteLog($"  Interval: {settings.SyncIntervalMinutes} min");
        WriteLog("");

        if (!onceMode)
            WriteLog("  Press Ctrl+C to stop. Auto-sync every " + $"{settings.SyncIntervalMinutes} minutes.\n");

        var lastSync = DateTime.MinValue;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await RunSync(settings, lastSync, cts.Token);
                lastSync = DateTime.UtcNow;
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Sync complete. Next sync in {settings.SyncIntervalMinutes} min.\n");
            }
            catch (Exception ex)
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}\n");
            }

            if (onceMode) break;
            try { await Task.Delay(settings.SyncIntervalMinutes * 60 * 1000, cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        WriteLog("  Ukuu HR Access Sync Bridge stopped.");
        return 0;
    }

    static void WriteLog(string message)
    {
        try { Console.WriteLine(message); } catch { }
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sync: fetch from device → push to cloud
    // ═══════════════════════════════════════════════════════════════════════
    static async Task RunSync(SyncSettings settings, DateTime lastSync, CancellationToken ct)
    {
        var scheme = settings.UseHttps.GetValueOrDefault(false) ? "https" : "http";
        var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connecting to device at {baseUrl}...");

        // ── Step 1: Get device info (validates connection + Digest auth) ─────
        string deviceName = "Unknown", deviceModel = "Unknown", serialNo = "";
        try
        {
            var infoResp = await _httpClient.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ct);
            if (infoResp.IsSuccessStatusCode)
            {
                var xml = await infoResp.Content.ReadAsStringAsync(ct);
                deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                serialNo = HikvisionParser.ExtractXmlValue(xml, "serialNo") ?? "";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] CONNECTED to {deviceName} ({deviceModel})");
                if (!string.IsNullOrEmpty(serialNo))
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Serial: {serialNo}");
            }
            else
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Device info HTTP {(int)infoResp.StatusCode}. Continuing...");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Device info failed: {ex.Message}. Continuing...");
        }

        // ── Step 2: Fetch access events via 3-tier ISAPI fallback ──────────
        var fromTime = lastSync == DateTime.MinValue ? DateTime.UtcNow.AddDays(-7) : lastSync;
        var toTime = DateTime.UtcNow;
        var fromStr = fromTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var toStr = toTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Pulling access records...");
        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Date range: {fromTime:yyyy-MM-dd HH:mm} to {toTime:yyyy-MM-dd HH:mm} ({(toTime - fromTime).Days} days)\n");

        List<ImportedPunch> events = new();
        var tier1Status = "";
        var tier2Status = "";
        var tier3Status = "";

        // ── Tier 1: AcsEvent JSON ──────────────────────────────────────────
        try
        {
            var searchBody = JsonSerializer.Serialize(new
            {
                AcsEventCond = new  // CORRECT wrapper (not AcsEventSearchDescription)
                {
                    searchID = "1",  // Must be STRING, not number
                    searchResultPosition = 0,
                    maxResults = 200,
                    major = 0,
                    minor = 0,
                    startTime = fromStr,
                    endTime = toStr
                }
            });

            var resp = await _httpClient.PostAsync(
                $"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json",
                new StringContent(searchBody, Encoding.UTF8, "application/json"), ct);

            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                events = HikvisionParser.ParseAcsEventJson(json);
                tier1Status = $"OK — {events.Count} records";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (JSON): {events.Count} records fetched");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var summary = Truncate(body, 120);
                tier1Status = $"HTTP {(int)resp.StatusCode} — {summary}";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (JSON): HTTP {(int)resp.StatusCode} — {summary}");
            }
        }
        catch (Exception ex)
        {
            tier1Status = $"Error — {ex.Message}";
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (JSON): {ex.Message}");
        }

        // ── Tier 2: AcsEvent XML ───────────────────────────────────────────
        if (events.Count == 0)
        {
            try
            {
                var xmlBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<AcsEventCond version=\"2.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">" +
                    "<searchID>1</searchID>" +
                    "<searchResultPosition>0</searchResultPosition>" +
                    "<maxResults>200</maxResults>" +
                    "<major>0</major><minor>0</minor>" +
                    $"<startTime>{fromStr}</startTime>" +
                    $"<endTime>{toStr}</endTime>" +
                    "</AcsEventCond>";

                var resp = await _httpClient.PostAsync(
                    $"{baseUrl}/ISAPI/AccessControl/AcsEvent",
                    new StringContent(xmlBody, Encoding.UTF8, "application/xml"), ct);

                if (resp.IsSuccessStatusCode)
                {
                    var xml = await resp.Content.ReadAsStringAsync(ct);
                    events = HikvisionParser.ParseAcsEventXml(xml);
                    tier2Status = $"OK — {events.Count} records";
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (XML): {events.Count} records fetched");
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    tier2Status = $"HTTP {(int)resp.StatusCode} — {Truncate(body, 120)}";
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (XML): HTTP {(int)resp.StatusCode} — {Truncate(body, 120)}");
                }
            }
            catch (Exception ex)
            {
                tier2Status = $"Error — {ex.Message}";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (XML): {ex.Message}");
            }
        }

        // ── Tier 3: AuditLog XML (legacy devices only) ─────────────────────
        if (events.Count == 0)
        {
            try
            {
                var xmlBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<AuditLogCond version=\"2.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">" +
                    "<searchID>1</searchID>" +
                    "<searchResultPosition>0</searchResultPosition>" +
                    "<maxResults>200</maxResults>" +
                    "<major>0</major><minor>0</minor>" +
                    $"<startTime>{fromStr}</startTime>" +
                    $"<endTime>{toStr}</endTime>" +
                    "</AuditLogCond>";

                var resp = await _httpClient.PostAsync(
                    $"{baseUrl}/ISAPI/System/AuditLog",
                    new StringContent(xmlBody, Encoding.UTF8, "application/xml"), ct);

                if (resp.IsSuccessStatusCode)
                {
                    var xml = await resp.Content.ReadAsStringAsync(ct);
                    events = HikvisionParser.ParseAuditLogXml(xml);
                    tier3Status = $"OK — {events.Count} records";
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] AuditLog: {events.Count} records fetched");
                }
                else
                {
                    tier3Status = $"HTTP {(int)resp.StatusCode}";
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] AuditLog: HTTP {(int)resp.StatusCode} (not supported)");
                }
            }
            catch (Exception ex)
            {
                tier3Status = $"Error — {ex.Message}";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AuditLog: {ex.Message}");
            }
        }

        // ── Summary ─────────────────────────────────────────────────────────
        if (events.Count == 0)
        {
            WriteLog($"\n  All event endpoints attempted:");
            WriteLog($"    AcsEvent JSON: {tier1Status}");
            WriteLog($"    AcsEvent XML:  {tier2Status}");
            WriteLog($"    AuditLog:      {tier3Status}");
            WriteLog($"\n  No access records found in the last {(toTime - fromTime).Days} days.");
            return;
        }

        WriteLog($"\n  [{DateTime.Now:HH:mm:ss}] Fetched {events.Count} access records. Pushing to cloud...");

        // ── Step 3: Push events to the cloud API ────────────────────────────
        var payload = JsonSerializer.Serialize(new
        {
            events = events,
            deviceInfo = new { name = deviceName, model = deviceModel, serial = serialNo }
        });

        try
        {
            var cloudUrl = settings.CloudUrl!.TrimEnd('/') + "/api/access/save-imported";
            var request = new HttpRequestMessage(HttpMethod.Post, cloudUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(settings.ApiKey))
                request.Headers.Add("X-API-Key", settings.ApiKey);

            var cloudResp = await _httpClient.SendAsync(request, ct);
            var cloudJson = await cloudResp.Content.ReadAsStringAsync(ct);

            if (cloudResp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(cloudJson);
                    var root = doc.RootElement;
                    int fetched = 0, matched = 0, imported = 0;
                    if (root.TryGetProperty("eventsFetched", out var ef) && ef.ValueKind == JsonValueKind.Number)
                        fetched = ef.GetInt32();
                    if (root.TryGetProperty("employeesMatched", out var em) && em.ValueKind == JsonValueKind.Number)
                        matched = em.GetInt32();
                    if (root.TryGetProperty("recordsImported", out var ri) && ri.ValueKind == JsonValueKind.Number)
                        imported = ri.GetInt32();
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud sync: {fetched} events, {matched} matched, {imported} imported.");
                }
                catch (JsonException)
                {
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud sync OK (response: {Truncate(cloudJson, 200)})");
                }
            }
            else
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud API error (HTTP {(int)cloudResp.StatusCode}): {Truncate(cloudJson, 200)}");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] ERROR pushing to cloud: {ex.Message}");
        }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    // ═══════════════════════════════════════════════════════════════════════
    // Settings
    // ═══════════════════════════════════════════════════════════════════════
    static SyncSettings? LoadOrCreateSettings(string path, bool headlessMode)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<SyncSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null && loaded.IsValid())
                {
                    WriteLog($"  Loaded settings from: {path}");
                    return loaded;
                }
                WriteLog($"  WARNING: settings.json invalid. Using defaults.");
            }
            catch (Exception ex)
            {
                WriteLog($"  WARNING: Could not load settings.json: {ex.Message}");
            }
        }

        if (headlessMode || !HasTty())
        {
            var defaults = new SyncSettings();
            try
            {
                var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                File.WriteAllText(path, json);
                WriteLog($"\n  No settings.json found. Created default at: {path}");
                WriteLog("  Edit this file with your Hikvision device details, then restart.");
            }
            catch (Exception ex) { WriteLog($"\n  WARNING: Could not save default settings: {ex.Message}"); }
            return defaults;
        }

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
            DeviceIp = ip, DevicePort = port, UseHttps = https,
            DeviceUsername = user, DevicePassword = pass,
            CloudUrl = cloudUrl, ApiKey = apiKey, SyncIntervalMinutes = interval
        };

        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(path, json);
            Console.WriteLine($"\n  Settings saved to: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"\n  WARNING: Could not save settings: {ex.Message}"); }

        return settings;
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

    static void PrintBanner()
    {
        var lines = new[]
        {
            "",
            "  ╔═══════════════════════════════════════════════╗",
            "  ║       UKUU HR — ACCESS SYNC BRIDGE v2.4.0     ║",
            "  ╠═══════════════════════════════════════════════╣",
            "  ║  Syncs Hikvision access data to Ukuu HR cloud  ║",
            "  ╚═══════════════════════════════════════════════╝",
            ""
        };
        foreach (var line in lines)
        {
            try { Console.WriteLine(line); } catch { }
        }
    }
}

public class SyncSettings
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
