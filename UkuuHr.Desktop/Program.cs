using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UkuuHr.Sync;

/// <summary>
/// Ukuu HR Sync Bridge
/// 
/// A cross-platform desktop application that:
/// 1. Connects to a Hikvision biometric terminal via ISAPI (on the local network)
/// 2. Fetches attendance events (clock-in/out punches)
/// 3. Pushes them to the Ukuu HR cloud API (ukuuhr.com)
/// 4. Auto-syncs on a configurable interval (default: every 5 minutes)
/// 
/// Usage:
///   UkuuHrSync                    — interactive setup, then continuous sync
///   UkuuHrSync --once             — single sync, then exit
///   UkuuHrSync --config=settings.json  — use a config file
/// 
/// The app reads/writes settings.json in the same directory for persistence.
/// </summary>
class Program
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        bool onceMode = args.Contains("--once");
        string? configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?["--config=".Length..];
        configPath ??= Path.Combine(AppContext.BaseDirectory, "settings.json");

        PrintBanner();

        // ── Load or create settings ──────────────────────────────────────────
        var settings = LoadOrCreateSettings(configPath);
        if (settings == null) return 1;

        var scheme = settings.UseHttps.GetValueOrDefault(true) ? "https" : "http";
        Console.WriteLine($"\n  Device:   {scheme}://{settings.DeviceIp}:{settings.DevicePort}");
        Console.WriteLine($"  Username: {settings.DeviceUsername}");
        Console.WriteLine($"  Cloud:    {settings.CloudUrl}");
        Console.WriteLine($"  Interval: {settings.SyncIntervalMinutes} min");
        Console.WriteLine();

        if (!onceMode)
        {
            Console.WriteLine("  Press Ctrl+C to stop. The bridge will auto-sync every " +
                $"{settings.SyncIntervalMinutes} minutes.\n");
        }

        // ── Sync loop ────────────────────────────────────────────────────────
        var lastSync = DateTime.MinValue;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await RunSync(settings, lastSync, cts.Token);
                lastSync = DateTime.UtcNow;
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Sync complete. Next sync in {settings.SyncIntervalMinutes} min.\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}\n");
            }

            if (onceMode) break;

            try { await Task.Delay(settings.SyncIntervalMinutes * 60 * 1000, cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        Console.WriteLine("  Ukuu HR Sync Bridge stopped.");
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sync: fetch from device → push to cloud
    // ═══════════════════════════════════════════════════════════════════════
    static async Task RunSync(SyncSettings settings, DateTime lastSync, CancellationToken ct)
    {
        var scheme = settings.UseHttps.GetValueOrDefault(true) ? "https" : "http";
        var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.DeviceUsername}:{settings.DevicePassword}"));

        Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Connecting to device at {baseUrl}...");

        // ── Step 1: Get device info (validates connection) ───────────────────
        string deviceName = "Unknown", deviceModel = "Unknown";
        try
        {
            var infoResp = await _httpClient.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ct);
            if (!infoResp.IsSuccessStatusCode)
            {
                // Try with auth header
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
                infoResp = await _httpClient.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ct);
            }
            if (infoResp.IsSuccessStatusCode)
            {
                var xml = await infoResp.Content.ReadAsStringAsync(ct);
                deviceName = ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                deviceModel = ExtractXmlValue(xml, "model") ?? "Unknown";
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Connected: {deviceName} ({deviceModel})");
            }
            else
            {
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] WARNING: Could not get device info (HTTP {(int)infoResp.StatusCode}). Continuing with event fetch...");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] WARNING: Device info probe failed: {ex.Message}. Continuing...");
        }

        // ── Step 2: Fetch attendance events via AcsEvent ─────────────────────
        var fromTime = lastSync == DateTime.MinValue
            ? DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ")
            : lastSync.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var toTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var searchBody = JsonSerializer.Serialize(new
        {
            AcsEventSearchDescription = new
            {
                searchID = $"sync_{DateTime.UtcNow:yyyyMMddHHmmss}",
                searchResultPosition = 0,
                maxResults = 1000,
                major = 0,
                minor = 0,
                startTime = fromTime,
                endTime = toTime
            }
        });

        List<ImportedEvent> events = new();
        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            var content = new StringContent(searchBody, Encoding.UTF8, "application/json");
            var eventResp = await _httpClient.PostAsync($"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json", content, ct);

            if (!eventResp.IsSuccessStatusCode)
            {
                // Fallback: try AuditLog XML endpoint
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] AcsEvent returned HTTP {(int)eventResp.StatusCode}, trying AuditLog...");
                var auditUrl = $"{baseUrl}/ISAPI/AccessControl/AuditLog/search?searchID=sync_{DateTime.UtcNow:yyyyMMddHHmmss}&startTime={Uri.EscapeDataString(fromTime)}&endTime={Uri.EscapeDataString(toTime)}&maxResults=1000";
                var auditResp = await _httpClient.GetAsync(auditUrl, ct);
                if (auditResp.IsSuccessStatusCode)
                {
                    var auditXml = await auditResp.Content.ReadAsStringAsync(ct);
                    events = ParseAuditLogXml(auditXml);
                }
                else
                {
                    throw new Exception($"Both AcsEvent (HTTP {(int)eventResp.StatusCode}) and AuditLog (HTTP {(int)auditResp.StatusCode}) failed.");
                }
            }
            else
            {
                var json = await eventResp.Content.ReadAsStringAsync(ct);
                events = ParseAcsEventJson(json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] ERROR fetching events: {ex.Message}");
            return;
        }

        if (events.Count == 0)
        {
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] No new attendance events found (range: {fromTime} to {toTime}).");
            return;
        }

        Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Fetched {events.Count} attendance events. Pushing to cloud...");

        // ── Step 3: Push events to the cloud API ────────────────────────────
        var payload = JsonSerializer.Serialize(new
        {
            events = events,
            deviceInfo = new { name = deviceName, model = deviceModel, serial = "" },
            faceRecognition = (object?)null
        });

        try
        {
            var cloudContent = new StringContent(payload, Encoding.UTF8, "application/json");
            var cloudUrl = settings.CloudUrl!.TrimEnd('/') + "/api/attendance/save-imported";

            // Add API key header if configured
            var request = new HttpRequestMessage(HttpMethod.Post, cloudUrl) { Content = cloudContent };
            if (!string.IsNullOrEmpty(settings.ApiKey))
                request.Headers.Add("X-API-Key", settings.ApiKey);

            var cloudResp = await _httpClient.SendAsync(request, ct);
            var cloudJson = await cloudResp.Content.ReadAsStringAsync(ct);

            if (cloudResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(cloudJson);
                var root = doc.RootElement;
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Cloud sync complete: " +
                    $"{root.GetProperty("eventsFetched").GetInt32()} events, " +
                    $"{root.GetProperty("employeesMatched").GetInt32()} matched, " +
                    $"{root.GetProperty("recordsImported").GetInt32()} imported.");
            }
            else
            {
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Cloud API error (HTTP {(int)cloudResp.StatusCode}): {cloudJson[..Math.Min(cloudJson.Length, 200)]}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] ERROR pushing to cloud: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ISAPI event parsers
    // ═══════════════════════════════════════════════════════════════════════
    static List<ImportedEvent> ParseAcsEventJson(string json)
    {
        var events = new List<ImportedEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement infoList = default;

            if (doc.RootElement.TryGetProperty("AcsEvent", out var acsEvent))
            {
                if (!acsEvent.TryGetProperty("InfoList", out infoList))
                    acsEvent.TryGetProperty("EventList", out infoList);
            }
            if (infoList.ValueKind != JsonValueKind.Array)
            {
                doc.RootElement.TryGetProperty("InfoList", out infoList) ||
                doc.RootElement.TryGetProperty("EventList", out infoList);
            }

            if (infoList.ValueKind != JsonValueKind.Array) return events;

            foreach (var item in infoList.EnumerateArray())
            {
                var empNo = item.TryGetProperty("employeeNo", out var en) ? en.GetString() ?? "" :
                            item.TryGetProperty("EmployeeNo", out var en2) ? en2.GetString() ?? "" : "";
                var time = item.TryGetProperty("time", out var t) ? t.GetString() ?? "" :
                           item.TryGetProperty("eventTime", out var t2) ? t2.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(empNo) || string.IsNullOrEmpty(time)) continue;

                var minor = item.TryGetProperty("minor", out var min) ? min.GetInt32() : 75;
                events.Add(new ImportedEvent
                {
                    EmployeeNo = empNo,
                    Time = time,
                    EventType = minor == 76 ? "check_out" : "check_in",
                    Major = 1,
                    Minor = minor
                });
            }
        }
        catch { }
        return events;
    }

    static List<ImportedEvent> ParseAuditLogXml(string xml)
    {
        var events = new List<ImportedEvent>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var item in doc.Descendants("LogItem"))
            {
                var empNo = item.Element("employeeNo")?.Value ?? "";
                var time = item.Element("time")?.Value ?? "";
                if (string.IsNullOrEmpty(empNo) || string.IsNullOrEmpty(time)) continue;

                var minorStr = item.Element("minor")?.Value ?? "75";
                var minor = int.TryParse(minorStr, out var m) ? m : 75;
                events.Add(new ImportedEvent
                {
                    EmployeeNo = empNo,
                    Time = time,
                    EventType = minor == 76 ? "check_out" : "check_in",
                    Major = 1,
                    Minor = minor
                });
            }
        }
        catch { }
        return events;
    }

    static string? ExtractXmlValue(string xml, string tagName)
    {
        var start = $"<{tagName}>";
        var end = $"</{tagName}>";
        var s = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (s < 0) return null;
        s += start.Length;
        var e = xml.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
        return e < 0 ? null : xml[s..e].Trim();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Settings
    // ═══════════════════════════════════════════════════════════════════════
    static SyncSettings? LoadOrCreateSettings(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<SyncSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (settings != null && settings.IsValid())
                {
                    Console.WriteLine($"  Loaded settings from: {path}");
                    return settings;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARNING: Could not load settings.json: {ex.Message}");
            }
        }

        // Interactive setup
        Console.WriteLine("  First-time setup — enter your Hikvision device details:\n");

        Console.Write("  Device IP Address [192.168.1.137]: ");
        var ip = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(ip)) ip = "192.168.1.137";

        Console.Write("  Port [80]: ");
        var portStr = Console.ReadLine()?.Trim();
        int port = int.TryParse(portStr, out var p) ? p : 80;

        Console.Write("  Use HTTPS? (y/n) [n]: ");
        var https = Console.ReadLine()?.Trim().ToLower() == "y";

        Console.Write("  Username [admin]: ");
        var user = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(user)) user = "admin";

        Console.Write("  Password: ");
        var pass = ReadPassword();

        Console.Write("  Cloud URL [https://ukuuhr.com]: ");
        var cloudUrl = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(cloudUrl)) cloudUrl = "https://ukuuhr.com";

        Console.Write("  API Key (leave empty if not set): ");
        var apiKey = Console.ReadLine()?.Trim();

        Console.Write("  Sync interval in minutes [5]: ");
        var intervalStr = Console.ReadLine()?.Trim();
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

        // Save settings
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
        catch (Exception ex)
        {
            Console.WriteLine($"\n  WARNING: Could not save settings: {ex.Message}");
        }

        return settings;
    }

    static string ReadPassword()
    {
        var pass = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
            {
                pass.Remove(pass.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (key.KeyChar != '\0')
            {
                pass.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        Console.WriteLine();
        return pass.ToString();
    }

    static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔═══════════════════════════════════════════════╗");
        Console.WriteLine("  ║         UKUU HR — SYNC BRIDGE v1.0.0          ║");
        Console.WriteLine("  ╠═══════════════════════════════════════════════╣");
        Console.WriteLine("  ║  Connects Hikvision devices to Ukuu HR cloud  ║");
        Console.WriteLine("  ╚═══════════════════════════════════════════════╝");
        Console.WriteLine();
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

class ImportedEvent
{
    public string EmployeeNo { get; set; } = "";
    public string Time { get; set; } = "";
    public string EventType { get; set; } = "check_in";
    public int Major { get; set; } = 1;
    public int Minor { get; set; } = 75;
}
