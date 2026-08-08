using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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
///   UkuuHrSync --stop             — signal a running headless instance to stop
///   UkuuHrSync --status           — check if a headless instance is running
/// 
/// The app reads/writes settings.json in the same directory for persistence.
/// When launched as a macOS .app bundle (no TTY), it runs in headless mode
/// automatically and creates a default settings.json if none exists.
/// 
/// Session lifecycle:
///   - A PID file (ukuu-sync.pid) is written on start and removed on clean exit.
///   - A stop-signal file (ukuu-sync.stop) is checked every sync cycle; writing
///     this file causes the running instance to end its connection session gracefully.
///   - On macOS, AppDomain.ProcessExit handles SIGTERM (Dock → Quit) so the
///     bridge shuts down cleanly even without a TTY.
///   - The shared HttpClient is disposed on shutdown to release TCP connections.
/// </summary>
class Program
{
    private static HttpClient? _httpClient;
    private static readonly string _pidPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync.pid");
    private static readonly string _stopSignalPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync.stop");
    private static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync.log");

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
        // ── Handle --stop: signal a running headless instance to end its session ──
        if (args.Contains("--stop"))
        {
            return HandleStopCommand();
        }

        // ── Handle --status: check if a headless instance is running ──────────
        if (args.Contains("--status"))
        {
            return HandleStatusCommand();
        }

        // ── Top-level exception handler — prevents SIGABRT on unhandled exceptions ──
        try
        {
            return await RunApp(args);
        }
        catch (Exception ex)
        {
            // Last-resort catch: log to file and console, then exit cleanly
            var errorLogPath = Path.Combine(AppContext.BaseDirectory, "ukuu-sync-error.log");
            try
            {
                await File.WriteAllTextAsync(errorLogPath,
                    $"[{DateTime.UtcNow:O}] FATAL: {ex}\n");
            }
            catch { /* give up */ }

            try { Console.Error.WriteLine($"FATAL: {ex.Message}"); }
            catch { /* no console */ }

            return 2;
        }
        finally
        {
            // ── Always clean up: dispose HttpClient, remove PID file ──────────
            CleanupOnExit();
        }
    }

    /// <summary>
    /// Handles the --stop command: writes a stop-signal file that the running
    /// headless instance checks on every cycle, then attempts to signal via
    /// PID if the signal file isn't picked up within a timeout.
    /// </summary>
    static int HandleStopCommand()
    {
        // Check if a running instance exists
        if (!File.Exists(_pidPath))
        {
            try { Console.WriteLine("No running UkuuHrSync instance found (PID file does not exist)."); }
            catch { }
            return 1;
        }

        int pid;
        try { pid = int.Parse(File.ReadAllText(_pidPath).Trim()); }
        catch
        {
            try { Console.WriteLine("Could not read PID file. The instance may have exited uncleanly."); }
            catch { }
            // Remove stale PID file
            try { File.Delete(_pidPath); } catch { }
            return 1;
        }

        // Write the stop-signal file — the running instance checks this
        try
        {
            File.WriteAllText(_stopSignalPath, $"[{DateTime.UtcNow:O}] Stop requested by --stop command (PID {pid})");
            try { Console.WriteLine($"Stop signal written. Waiting for instance (PID {pid}) to end session..."); }
            catch { }
        }
        catch (Exception ex)
        {
            try { Console.WriteLine($"Could not write stop signal: {ex.Message}"); }
            catch { }
            return 1;
        }

        // Wait up to 15 seconds for the instance to exit
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(500);
            if (!File.Exists(_pidPath))
            {
                try { Console.WriteLine("Instance stopped successfully."); } catch { }
                // Clean up stop-signal file
                try { File.Delete(_stopSignalPath); } catch { }
                return 0;
            }
        }

        // Instance didn't stop gracefully — try sending SIGTERM on Unix
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(); // Sends SIGKILL on Unix
                try { Console.WriteLine($"Instance (PID {pid}) did not stop gracefully — force killed."); } catch { }
                try { File.Delete(_pidPath); } catch { }
                try { File.Delete(_stopSignalPath); } catch { }
                return 0;
            }
            catch
            {
                try { Console.WriteLine($"Could not kill process {pid}. It may have already exited."); } catch { }
                try { File.Delete(_pidPath); } catch { }
                return 1;
            }
        }

        try { Console.WriteLine("Timeout waiting for instance to stop. Try closing it manually."); } catch { }
        return 1;
    }

    /// <summary>
    /// Handles the --status command: checks PID file and verifies the process
    /// is still alive.
    /// </summary>
    static int HandleStatusCommand()
    {
        if (!File.Exists(_pidPath))
        {
            try { Console.WriteLine("No running UkuuHrSync instance found."); } catch { }
            return 1;
        }

        try
        {
            var pid = int.Parse(File.ReadAllText(_pidPath).Trim());
            var process = System.Diagnostics.Process.GetProcessById(pid);
            try
            {
                Console.WriteLine($"UkuuHrSync is running (PID {pid}, started: {process.StartTime:yyyy-MM-dd HH:mm:ss})");
                Console.WriteLine($"Log file: {_logPath}");
            }
            catch { }
            return 0;
        }
        catch
        {
            try { Console.WriteLine("PID file exists but process is not running (stale PID file)."); } catch { }
            try { File.Delete(_pidPath); } catch { }
            return 1;
        }
    }

    /// <summary>
    /// Writes the PID file on startup. Prevents duplicate instances.
    /// Returns false if another instance is already running.
    /// </summary>
    static bool WritePidFile()
    {
        // Check for existing PID file from a previous instance
        if (File.Exists(_pidPath))
        {
            try
            {
                var existingPid = int.Parse(File.ReadAllText(_pidPath).Trim());
                var existingProcess = System.Diagnostics.Process.GetProcessById(existingPid);
                // Process still alive — don't start a second instance
                WriteLog($"Another UkuuHrSync instance is already running (PID {existingPid}). Stopping.");
                return false;
            }
            catch
            {
                // Process is gone — stale PID file, safe to remove
                try { File.Delete(_pidPath); } catch { }
            }
        }

        try
        {
            File.WriteAllText(_pidPath, Environment.ProcessId.ToString());
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"WARNING: Could not write PID file: {ex.Message}");
            return true; // Non-fatal — continue without PID file
        }
    }

    /// <summary>
    /// Checks if a stop-signal file exists (written by --stop command).
    /// If found, deletes it and returns true to indicate the session should end.
    /// </summary>
    static bool CheckStopSignal()
    {
        if (!File.Exists(_stopSignalPath)) return false;

        try
        {
            File.Delete(_stopSignalPath);
            WriteLog("Stop signal received (ukuu-sync.stop). Ending connection session...");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cleanup on exit: dispose HttpClient to close TCP connections, remove PID file.
    /// </summary>
    static void CleanupOnExit()
    {
        // Dispose the shared HttpClient — releases any lingering TCP connections
        // to the Hikvision device and the cloud API, preventing CLOSE_WAIT sockets.
        try { _httpClient?.Dispose(); _httpClient = null; } catch { }

        // Remove PID file so --status no longer reports this instance
        try { File.Delete(_pidPath); } catch { }

        // Remove stop-signal file (in case it wasn't consumed)
        try { File.Delete(_stopSignalPath); } catch { }

        // Write shutdown marker to log
        try
        {
            File.AppendAllText(_logPath,
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] === UkuuHrSync shutdown complete (PID {Environment.ProcessId}) ===\n");
        }
        catch { }
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

        // ── Prevent duplicate instances ──────────────────────────────────────
        if (!onceMode && !WritePidFile())
        {
            return 1;
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

        // ── Initialize the shared HttpClient with digest auth support ──────────
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
        _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/xml"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

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

        // ── Sync loop with graceful shutdown ────────────────────────────────
        var lastSync = DateTime.MinValue;
        var cts = new CancellationTokenSource();

        // Handle Ctrl+C (SIGINT) — works when TTY is attached
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; // Don't kill the process — let the loop exit gracefully
            WriteLog("Ctrl+C received — ending connection session...");
            cts.Cancel();
        };

        // Handle SIGTERM / ProcessExit — critical for macOS Dock → Quit and
        // Windows Task Manager "End Task". On Unix, AppDomain.ProcessExit fires
        // for SIGTERM; on Windows it fires for normal process shutdown.
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                WriteLog("Process exit signal received — ending connection session...");
                cts.Cancel();
            }
        };

        // On Unix (macOS/Linux), also register a POSIX signal handler for SIGTERM
        // for more reliable shutdown when the .app bundle is quit from the Dock.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // .NET 7+ POSIX signal handling
                System.Runtime.InteropServices.NativeLibrary.TryLoad("libc", out var _);
                var sigterm = PosixSignal.SIGTERM;
                PosixSignalRegistration.Create(sigterm, sig =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        WriteLog("SIGTERM received — ending connection session...");
                        cts.Cancel();
                    }
                });
            }
            catch
            {
                // POSIX signal handling not available (older .NET or Windows) — 
                // ProcessExit handler above will catch SIGTERM on most runtimes
            }
        }

        WriteLog($"  Bridge started (PID {Environment.ProcessId}). Session active.\n");

        while (!cts.Token.IsCancellationRequested)
        {
            // ── Check for external stop signal (from --stop command) ──────────
            if (CheckStopSignal())
            {
                cts.Cancel();
                break;
            }

            try
            {
                await RunSync(settings, lastSync, cts.Token);
                lastSync = DateTime.UtcNow;
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Sync complete. Next sync in {settings.SyncIntervalMinutes} min.\n");
            }
            catch (OperationCanceledException)
            {
                break; // CTS cancelled — exit the loop
            }
            catch (Exception ex)
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}\n");
            }

            if (onceMode) break;

            try { await Task.Delay(settings.SyncIntervalMinutes * 60 * 1000, cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        WriteLog("  Ukuu HR Sync Bridge stopped. Connection session ended.");
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
        if (_httpClient == null)
        {
            WriteLog("  ERROR: HttpClient not initialized. Cannot sync.");
            return;
        }

        var scheme = settings.UseHttps.GetValueOrDefault() ? "https" : "http";
        var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";

        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connecting to device at {baseUrl}...");

        // ── Step 1: Get device info (validates connection) ───────────────────
        // Digest auth is handled automatically by HttpClientHandler (PreAuthenticate + Credentials).
        // Uses a shorter timeout (10s) for the probe to avoid long waits on
        // misconfigured HTTPS→HTTP connections. Falls back to HTTP if HTTPS
        // times out on a standard HTTP port (80/8080).
        string deviceName = "Unknown", deviceModel = "Unknown";
        try
        {
            // Use a shorter timeout for the initial probe to detect misconfig faster
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(10));

            var infoResp = await _httpClient.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", probeCts.Token);
            if (infoResp.IsSuccessStatusCode)
            {
                var xml = await infoResp.Content.ReadAsStringAsync(ct);
                deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connected: {deviceName} ({deviceModel})");
            }
            else
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: Could not get device info (HTTP {(int)infoResp.StatusCode}). Continuing with event fetch...");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Probe timed out (10s) but app wasn't cancelled — likely HTTPS on an HTTP-only port
            if (scheme == "https" && (settings.DevicePort == 80 || settings.DevicePort == 8080))
            {
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: HTTPS probe timed out on port {settings.DevicePort}. Most Hikvision devices use HTTP on this port.");
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] Falling back to HTTP... (Set useHttps=false in settings.json to fix this permanently)");

                // Retry with HTTP
                scheme = "http";
                baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";
                try
                {
                    using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    retryCts.CancelAfter(TimeSpan.FromSeconds(10));
                    var retryResp = await _httpClient.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", retryCts.Token);
                    if (retryResp.IsSuccessStatusCode)
                    {
                        var xml = await retryResp.Content.ReadAsStringAsync(ct);
                        deviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                        deviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] Connected via HTTP: {deviceName} ({deviceModel})");
                    }
                    else
                    {
                        WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: HTTP fallback also failed (HTTP {(int)retryResp.StatusCode}). Continuing...");
                    }
                }
                catch (Exception retryEx)
                {
                    WriteLog($"  [{DateTime.Now:HH:mm:ss}] WARNING: HTTP fallback probe failed: {retryEx.Message}. Continuing...");
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
            // Auth handled automatically by HttpClientHandler (digest auth)
            // Send XML body, request JSON response via ?format=json
            var content = new StringContent(searchXml, Encoding.UTF8, "application/xml");
            var eventResp = await _httpClient.PostAsync($"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json", content, ct);

            if (!eventResp.IsSuccessStatusCode)
            {
                // Fallback: try AuditLog XML endpoint
                WriteLog($"  [{DateTime.Now:HH:mm:ss}] AcsEvent returned HTTP {(int)eventResp.StatusCode}, trying AuditLog...");
                // Use searchID=1 (matching the Web client's working implementation).
                // Omit maxResults from the URL — it's not a valid query param for
                // AuditLog/search and causes HTTP 404 on some Hikvision models.
                var auditUrl = $"{baseUrl}/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime={Uri.EscapeDataString(fromTime)}&endTime={Uri.EscapeDataString(toTime)}";
                var auditResp = await _httpClient.GetAsync(auditUrl, ct);
                if (auditResp.IsSuccessStatusCode)
                {
                    var auditXml = await auditResp.Content.ReadAsStringAsync(ct);
                    events = HikvisionParser.ParseAuditLogXml(auditXml);
                }
                else
                {
                    throw new Exception($"Both AcsEvent (HTTP {(int)eventResp.StatusCode}) and AuditLog (HTTP {(int)auditResp.StatusCode}) failed.");
                }
            }
            else
            {
                var json = await eventResp.Content.ReadAsStringAsync(ct);
                events = HikvisionParser.ParseAcsEventJson(json);
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
            "  ║         UKUU HR — SYNC BRIDGE v1.3.1          ║",
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
