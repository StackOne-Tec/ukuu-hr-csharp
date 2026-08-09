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
///   UkuuHrSync --headless         — non-interactive mode (requires settings.json)
/// 
/// The app reads/writes settings.json in the same directory for persistence.
/// When launched as a macOS .app bundle (no TTY), it runs in headless mode
/// automatically and creates a default settings.json if none exists.
/// </summary>
class Program
{
    private static HttpClient? _httpClient;

    /// <summary>
    /// Detects whether stdin is connected to a terminal (TTY).
    /// On macOS, when the .app is launched from Finder, no TTY is attached
    /// and Console.ReadLine() / Console.ReadKey() throw IOException.
    /// </summary>
    private static bool HasTty()
    {
        try
        {
            // On Unix/macOS, try to check if stdin is a terminal
            if (Console.IsInputRedirected) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task<int> Main(string[] args)
    {
        // ── Top-level exception handler — prevents SIGABRT on unhandled exceptions ──
        try
        {
            return await RunApp(args);
        }
        catch (Exception ex)
        {
            // Last-resort catch: log to file and console, then exit cleanly
            var logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync-error.log");
            try
            {
                await File.WriteAllTextAsync(logPath,
                    $"[{DateTime.UtcNow:O}] FATAL: {ex}\n");
            }
            catch { /* give up */ }

            try { Console.Error.WriteLine($"FATAL: {ex.Message}"); }
            catch { /* no console */ }

            return 2;
        }
    }

    static async Task<int> RunApp(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { /* no console — headless mode */ }

        bool onceMode = args.Contains("--once");
        bool headlessMode = args.Contains("--headless");
        string? configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?["--config=".Length..];
        configPath ??= Path.Combine(AppContext.BaseDirectory, "settings.json");

        // ── Auto-detect headless mode (no TTY = launched from Finder) ───────
        if (!headlessMode && !HasTty())
        {
            headlessMode = true;
            WriteLog("Running in headless mode (no TTY detected — likely launched from Finder/Dock).");
        }

        PrintBanner();

        // ── Load or create settings ──────────────────────────────────────────
        var settings = LoadOrCreateSettings(configPath, headlessMode);
        if (settings == null)
        {
            WriteLog($"No settings found at: {configPath}");
            WriteLog("Run from Terminal with: ./UkuuHrSync");
            WriteLog("Or create settings.json manually. See README for format.");
            return 1;
        }

        // ── Initialize HttpClient with digest auth support ────────────────────
        // Hikvision devices default to digest authentication (RFC 7616).
        // Setting PreAuthenticate=true + Credentials enables .NET's built-in
        // digest auth handler: on 401 with WWW-Authenticate: Digest challenge,
        // the handler automatically re-sends the request with the correct header.
        _httpClient = new HttpClient(new HttpClientHandler
        {
            // Biometric devices (Hikvision, ZKTeco, etc.) use self-signed certificates.
            // Bypass validation so HTTPS connections work without installing root CAs.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            // Enable digest authentication — Hikvision default auth mode
            PreAuthenticate = true,
            Credentials = new System.Net.NetworkCredential(settings.DeviceUsername, settings.DevicePassword)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Hikvision ISAPI devices require Accept headers to know which response
        // formats the client supports. Without these, some endpoints return HTTP 400.
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var scheme = settings.UseHttps.GetValueOrDefault() ? "https" : "http";
        WriteLog($"\n  Device:   {scheme}://{settings.DeviceIp}:{settings.DevicePort}");
        WriteLog($"  Username: {settings.DeviceUsername}");
        WriteLog($"  Cloud:    {settings.CloudUrl}");
        WriteLog($"  Interval: {settings.SyncIntervalMinutes} min");
        WriteLog("");

        if (!onceMode)
        {
            if (headlessMode)
            {
                WriteLog("  Running in headless mode. To end the session:");
                WriteLog("    • Run: ./UkuuHrSync --stop");
                WriteLog("    • Or on macOS: Quit from the Dock");
                WriteLog("    • Or on Windows: Close the terminal / Task Manager\n");
            }
            else
            {
                WriteLog("  Press Ctrl+C to end the session. The bridge will auto-sync every " +
                    $"{settings.SyncIntervalMinutes} minutes.\n");
            }
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

        WriteLog("  Ukuu HR Sync Bridge stopped.");
        return 0;
    }

    /// <summary>
    /// Write a log line to both the console (if available) and a persistent log file.
    /// This ensures messages are visible even in headless / no-TTY mode.
    /// </summary>
    static void WriteLog(string message)
    {
        // Write to console if possible
        try { Console.WriteLine(message); }
        catch { /* no console — ignore */ }

        // Always append to log file for headless debugging
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* can't write log — nothing more we can do */ }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sync: fetch from device → push to cloud
    // ═══════════════════════════════════════════════════════════════════════
    static async Task RunSync(SyncSettings settings, DateTime lastSync, CancellationToken ct)
    {
        // ── Protocol probe with HTTPS→HTTP fallback ─────────────────────────
        // Hikvision devices on port 80/8080 typically don't serve TLS.
        // If HTTPS fails (timeout/TLS error), fall back to HTTP automatically.
        var scheme = settings.UseHttps.GetValueOrDefault() ? "https" : "http";
        var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connecting to device at {baseUrl}...");

        // ── Step 1: Get device info (validates connection) ───────────────────
        string deviceName = "Unknown", deviceModel = "Unknown";
        try
        {
            using var ctsProbe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ctsProbe.CancelAfter(TimeSpan.FromSeconds(10)); // 10s probe timeout

            var infoResp = await _httpClient!.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ctsProbe.Token);
            if (infoResp.IsSuccessStatusCode)
            {
                var xml = await infoResp.Content.ReadAsStringAsync(ct);
                deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connected: {deviceName} ({deviceModel})");
            }
            else
            {
                // If HTTPS failed, try HTTP fallback for common non-TLS ports
                if (scheme == "https" && (settings.DevicePort == 80 || settings.DevicePort == 8080))
                {
                    var httpUrl = $"http://{settings.DeviceIp}:{settings.DevicePort}";
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] HTTPS failed (HTTP {(int)infoResp.StatusCode}), trying HTTP fallback...");
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(10));
                    var infoResp2 = await _httpClient.GetAsync($"{httpUrl}/ISAPI/System/deviceInfo", cts2.Token);
                    if (infoResp2.IsSuccessStatusCode)
                    {
                        var xml2 = await infoResp2.Content.ReadAsStringAsync(ct);
                        deviceName = HikvisionParser.ExtractXmlValue(xml2, "deviceName") ?? "Unknown";
                        deviceModel = HikvisionParser.ExtractXmlValue(xml2, "model") ?? "Unknown";
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connected (HTTP): {deviceName} ({deviceModel})");
                        baseUrl = httpUrl; // Use HTTP for subsequent requests
                    }
                    else
                    {
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Both HTTPS and HTTP failed. Continuing with event fetch...");
                    }
                }
                else
                {
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Could not get device info (HTTP {(int)infoResp.StatusCode}). Continuing with event fetch...");
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Probe timed out — likely wrong protocol or unreachable device
            if (scheme == "https" && (settings.DevicePort == 80 || settings.DevicePort == 8080))
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] HTTPS probe timed out (10s), trying HTTP fallback...");
                var httpUrl = $"http://{settings.DeviceIp}:{settings.DevicePort}";
                try
                {
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(10));
                    var infoResp = await _httpClient!.GetAsync($"{httpUrl}/ISAPI/System/deviceInfo", cts2.Token);
                    if (infoResp.IsSuccessStatusCode)
                    {
                        var xml = await infoResp.Content.ReadAsStringAsync(ct);
                        deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                        deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connected (HTTP): {deviceName} ({deviceModel})");
                        baseUrl = httpUrl;
                    }
                    else
                    {
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: HTTP fallback also failed (HTTP {(int)infoResp.StatusCode}). Continuing...");
                    }
                }
                catch (Exception ex2)
                {
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: HTTP fallback also failed: {ex2.Message}. Continuing...");
                }
            }
            else
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Device info probe timed out after 10s. Check device IP and network connectivity. Continuing...");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Device info probe failed: {ex.Message}. Continuing...");
        }

        // ── Step 2: Fetch attendance events via AcsEvent ─────────────────────
        // Hikvision ISAPI requires XML request body even when requesting JSON
        // response format via ?format=json. The Web app's HikvisionIsapiClient
        // uses the same approach (BuildAcsEventSearchXml).
        var fromTime = lastSync == DateTime.MinValue
            ? DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ")
            : lastSync.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var toTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var searchId = $"AcsEventSearch_{Guid.NewGuid():N}";

        var searchXml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEventSearchDescription>
    <searchID>{searchId}</searchID>
    <searchResultPosition>0</searchResultPosition>
    <maxResults>1000</maxResults>
    <major>1</major>
    <minor>0</minor>
    <startTime>{fromTime}</startTime>
    <endTime>{toTime}</endTime>
</AcsEventSearchDescription>";

        List<ImportedPunch> events = new();
        try
        {
            // ── Fallback chain: AcsEvent JSON → AcsEvent XML → AuditLog XML ──
            // Some Hikvision models (e.g. DS-K1T343EFWX) don't support ?format=json
            // on AcsEvent and return HTTP 400. Others may not support AuditLog at all.
            // We try each format and log the device's error body for diagnostics.

            // Attempt 1: AcsEvent with JSON response (?format=json)
            var content1 = new StringContent(searchXml, Encoding.UTF8, "application/xml");
            var resp1 = await _httpClient!.PostAsync($"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json", content1, ct);

            if (resp1.IsSuccessStatusCode)
            {
                var json = await resp1.Content.ReadAsStringAsync(ct);
                events = HikvisionParser.ParseAcsEventJson(json);
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (JSON) OK — {events.Count} events parsed.");
            }
            else
            {
                var errBody1 = await resp1.Content.ReadAsStringAsync(ct);
                var errSnippet1 = errBody1.Length > 300 ? errBody1[..300] : errBody1;
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (JSON) returned HTTP {(int)resp1.StatusCode}: {errSnippet1}");

                // Attempt 2: AcsEvent without ?format=json (XML response)
                var content2 = new StringContent(searchXml, Encoding.UTF8, "application/xml");
                var resp2 = await _httpClient.PostAsync($"{baseUrl}/ISAPI/AccessControl/AcsEvent", content2, ct);

                if (resp2.IsSuccessStatusCode)
                {
                    var xml = await resp2.Content.ReadAsStringAsync(ct);
                    events = HikvisionParser.ParseAcsEventXml(xml);
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (XML) OK — {events.Count} events parsed.");
                }
                else
                {
                    var errBody2 = await resp2.Content.ReadAsStringAsync(ct);
                    var errSnippet2 = errBody2.Length > 300 ? errBody2[..300] : errBody2;
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent (XML) returned HTTP {(int)resp2.StatusCode}: {errSnippet2}");

                    // Attempt 3: AuditLog (XML) fallback
                    var auditUrl = $"{baseUrl}/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime={Uri.EscapeDataString(fromTime)}&endTime={Uri.EscapeDataString(toTime)}";
                    var resp3 = await _httpClient.GetAsync(auditUrl, ct);

                    if (resp3.IsSuccessStatusCode)
                    {
                        var auditXml = await resp3.Content.ReadAsStringAsync(ct);
                        events = HikvisionParser.ParseAuditLogXml(auditXml);
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] AuditLog (XML) OK — {events.Count} events parsed.");
                    }
                    else
                    {
                        var errBody3 = await resp3.Content.ReadAsStringAsync(ct);
                        var errSnippet3 = errBody3.Length > 300 ? errBody3[..300] : errBody3;
                        throw new Exception($"All event endpoints failed:\n" +
                            $"  AcsEvent JSON: HTTP {(int)resp1.StatusCode} ({errSnippet1})\n" +
                            $"  AcsEvent XML:  HTTP {(int)resp2.StatusCode} ({errSnippet2})\n" +
                            $"  AuditLog XML:  HTTP {(int)resp3.StatusCode} ({errSnippet3})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] ERROR fetching events: {ex.Message}");
            return;
        }

        if (events.Count == 0)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] No new attendance events found (range: {fromTime} to {toTime}).");
            return;
        }

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Fetched {events.Count} attendance events. Pushing to cloud...");

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
                // ── Parse cloud response safely — never crash on unexpected JSON ──
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

                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud sync complete: " +
                        $"{fetched} events, {matched} matched, {imported} imported.");
                }
                catch (JsonException jex)
                {
                    // API returned 200 but unexpected JSON body — log but don't crash
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud sync OK (response: {cloudJson[..Math.Min(cloudJson.Length, 200)]})");
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] NOTE: Could not parse cloud response details: {jex.Message}");
                }
            }
            else
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Cloud API error (HTTP {(int)cloudResp.StatusCode}): {cloudJson[..Math.Min(cloudJson.Length, 200)]}");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"  [{DateTime.Now:HH:mm:ss}] ERROR pushing to cloud: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ISAPI event parsers — extracted to HikvisionParser.cs (unit-tested with
    // 1000+ cases; hardened against malformed payloads).
    // ═══════════════════════════════════════════════════════════════════════

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
                var loaded = JsonSerializer.Deserialize<SyncSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null && loaded.IsValid())
                {
                    WriteLog($"  Loaded settings from: {path}");
                    return loaded;
                }
                else
                {
                    WriteLog($"  WARNING: settings.json at {path} is invalid. Falling back to defaults.");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"  WARNING: Could not load settings.json: {ex.Message}");
            }
        }

        // ── Headless mode: create default settings.json and return it ────────
        // When launched from Finder (no TTY), we can't do interactive setup.
        // Write a default config that the user can edit manually.
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
                WriteLog($"\n  No settings.json found. Created default at: {path}");
                WriteLog("  Edit this file with your Hikvision device details, then restart.");
            }
            catch (Exception ex)
            {
                WriteLog($"\n  WARNING: Could not save default settings: {ex.Message}");
            }
            return defaults;
        }

        // ── Interactive setup (TTY available) ───────────────────────────────
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

    /// <summary>
    /// Safe wrapper for Console.ReadLine() — returns null instead of throwing
    /// when no TTY is attached (macOS .app bundle launched from Finder).
    /// </summary>
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
            catch (IOException)
            {
                // No TTY — can't read password interactively, return empty
                Console.WriteLine();
                return "";
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine();
                return "";
            }

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
            "  ║         UKUU HR — SYNC BRIDGE v1.3.4          ║",
            "  ╠═══════════════════════════════════════════════╣",
            "  ║  Connects Hikvision devices to Ukuu HR cloud  ║",
            "  ╚═══════════════════════════════════════════════╝",
            ""
        };
        foreach (var line in lines)
        {
            try { Console.WriteLine(line); }
            catch { /* no console */ }
        }
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
