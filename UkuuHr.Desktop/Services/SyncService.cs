using System.Net;
using System.Text;
using System.Text.Json;

namespace UkuuHr.Sync.Services;

/// <summary>
/// Background sync service that connects to Hikvision devices via ISAPI
/// and pushes access events to the Ukuu HR cloud API.
///
/// Extracted from the old console Program.cs into a reusable service
/// that can be driven by the Avalonia GUI (or headless CLI mode).
///
/// ISAPI 3-Tier Fallback:
///   Tier 1: AcsEvent JSON  — POST /ISAPI/AccessControl/AcsEvent?format=json
///   Tier 2: AcsEvent XML   — POST /ISAPI/AccessControl/AcsEvent (XML body)
///   Tier 3: AuditLog XML   — POST /ISAPI/System/AuditLog (legacy devices only)
///
/// Never crashes — all errors are caught and reported via SyncResult.
/// </summary>
public class SyncService : IDisposable
{
    private HttpClient? _httpClient;
    private CancellationTokenSource? _autoSyncCts;
    private bool _disposed;

    // ── Events for UI notification ───────────────────────────────────────────

    public event Action<SyncLogEntry>? LogAdded;
    public event Action<SyncResult>? SyncCompleted;
    public event Action<bool>? ConnectionStateChanged;

    // ── Current State ────────────────────────────────────────────────────────

    public string DeviceName { get; private set; } = "";
    public string DeviceModel { get; private set; } = "";
    public string SerialNo { get; private set; } = "";
    public bool IsConnected { get; private set; }
    public bool IsSyncing { get; private set; }
    public SyncResult? LastResult { get; private set; }
    public DateTime LastSyncTime { get; private set; }
    public int TotalRecordsSynced { get; private set; }

    // ── Log History ──────────────────────────────────────────────────────────

    private readonly List<SyncLogEntry> _logHistory = new();
    public IReadOnlyList<SyncLogEntry> LogHistory => _logHistory;

    // ═══════════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════════

    public void Configure(SyncSettings settings)
    {
        _httpClient?.Dispose();
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(settings.DeviceUsername, settings.DevicePassword),
            PreAuthenticate = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true // Self-signed certs common on local networks
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Test connection to the device without syncing.
    /// Returns true if device info was successfully retrieved.
    /// </summary>
    public async Task<bool> TestConnectionAsync(SyncSettings settings, CancellationToken ct = default)
    {
        Configure(settings);
        var scheme = settings.UseHttps.GetValueOrDefault(false) ? "https" : "http";
        var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";

        try
        {
            var resp = await _httpClient!.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ct);
            if (resp.IsSuccessStatusCode)
            {
                var xml = await resp.Content.ReadAsStringAsync(ct);
                DeviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                DeviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                SerialNo = HikvisionParser.ExtractXmlValue(xml, "serialNo") ?? "";
                IsConnected = true;
                ConnectionStateChanged?.Invoke(true);
                AddLog(LogLevel.Success, $"Connected to {DeviceName} ({DeviceModel})");
                return true;
            }
            IsConnected = false;
            ConnectionStateChanged?.Invoke(false);
            AddLog(LogLevel.Error, $"Device returned HTTP {(int)resp.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(false);
            AddLog(LogLevel.Error, $"Connection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run a full sync: fetch from device → push to cloud.
    /// Uses the date range from settings.
    /// Never throws — all errors are captured in SyncResult.
    /// </summary>
    public async Task<SyncResult> SyncAsync(SyncSettings settings, CancellationToken ct = default)
    {
        if (IsSyncing) return LastResult ?? new SyncResult { Success = false, ErrorMessage = "Sync already in progress" };

        IsSyncing = true;
        var result = new SyncResult { StartedAt = DateTime.UtcNow };

        try
        {
            Configure(settings);
            var scheme = settings.UseHttps.GetValueOrDefault(false) ? "https" : "http";
            var baseUrl = $"{scheme}://{settings.DeviceIp}:{settings.DevicePort}";

            AddLog(LogLevel.Info, "Starting sync...");

            // ── Step 1: Get device info ────────────────────────────────────────
            try
            {
                var infoResp = await _httpClient!.GetAsync($"{baseUrl}/ISAPI/System/deviceInfo", ct);
                if (infoResp.IsSuccessStatusCode)
                {
                    var xml = await infoResp.Content.ReadAsStringAsync(ct);
                    DeviceName = HikvisionParser.ExtractXmlValue(xml, "deviceName") ?? "Unknown";
                    DeviceModel = HikvisionParser.ExtractXmlValue(xml, "model") ?? "Unknown";
                    SerialNo = HikvisionParser.ExtractXmlValue(xml, "serialNo") ?? "";
                    IsConnected = true;
                    ConnectionStateChanged?.Invoke(true);
                    result.DeviceName = DeviceName;
                    result.DeviceModel = DeviceModel;
                    AddLog(LogLevel.Success, $"Connected to {DeviceName} ({DeviceModel})");
                }
                else
                {
                    AddLog(LogLevel.Warning, $"Device info HTTP {(int)infoResp.StatusCode}. Continuing...");
                }
            }
            catch (Exception ex)
            {
                AddLog(LogLevel.Warning, $"Device info failed: {ex.Message}. Continuing...");
            }

            // ── Step 2: Fetch access events with date range ────────────────────
            var (fromTime, toTime) = settings.GetDateRange();
            var fromStr = fromTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var toStr = toTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            result.FromDate = fromTime;
            result.ToDate = toTime;

            AddLog(LogLevel.Info, $"Date range: {fromTime:yyyy-MM-dd HH:mm} to {toTime:yyyy-MM-dd HH:mm} ({(toTime - fromTime).Days} days)");

            List<ImportedPunch> events = new();

            // ── Tier 1: AcsEvent JSON ──────────────────────────────────────────
            var (tier1Events, tier1Msg) = await TryAcsEventJson(baseUrl, fromStr, toStr, ct);
            result.Tier1Status = tier1Msg;
            AddLog(LogLevel.Info, $"AcsEvent (JSON): {tier1Msg}");
            if (tier1Events.Count > 0) events = tier1Events;

            // ── Tier 2: AcsEvent XML (only if Tier 1 found nothing) ────────────
            if (events.Count == 0)
            {
                var (tier2Events, tier2Msg) = await TryAcsEventXml(baseUrl, fromStr, toStr, ct);
                result.Tier2Status = tier2Msg;
                AddLog(LogLevel.Info, $"AcsEvent (XML): {tier2Msg}");
                if (tier2Events.Count > 0) events = tier2Events;
            }

            // ── Tier 3: AuditLog XML (only if both Tiers 1+2 found nothing) ────
            if (events.Count == 0)
            {
                var (tier3Events, tier3Msg) = await TryAuditLogXml(baseUrl, fromStr, toStr, ct);
                result.Tier3Status = tier3Msg;
                AddLog(LogLevel.Info, $"AuditLog: {tier3Msg}");
                if (tier3Events.Count > 0) events = tier3Events;
            }

            result.RecordsFetched = events.Count;
            result.Records = events;

            if (events.Count == 0)
            {
                result.Success = true;
                result.ErrorMessage = "No access records found in the selected date range.";
                AddLog(LogLevel.Warning, result.ErrorMessage);
                return result;
            }

            AddLog(LogLevel.Success, $"Fetched {events.Count} access records. Pushing to cloud...");

            // ── Step 3: Push to cloud API ──────────────────────────────────────
            var (cloudOk, cloudMsg) = await PushToCloud(settings, events, result, ct);
            result.CloudStatus = cloudMsg;

            if (cloudOk)
            {
                AddLog(LogLevel.Success, cloudMsg);
                TotalRecordsSynced += events.Count;
                result.Success = true;
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = cloudMsg;
                AddLog(LogLevel.Error, cloudMsg);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Sync was cancelled.";
            AddLog(LogLevel.Warning, "Sync cancelled.");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Unexpected error: {ex.Message}";
            AddLog(LogLevel.Error, $"Sync error: {ex.Message}");
            return result;
        }
        finally
        {
            result.CompletedAt = DateTime.UtcNow;
            LastResult = result;
            LastSyncTime = DateTime.UtcNow;
            IsSyncing = false;
            SyncCompleted?.Invoke(result);
        }
    }

    /// <summary>Start automatic sync on a timer.</summary>
    public void StartAutoSync(SyncSettings settings, Action<SyncResult>? onSync = null)
    {
        StopAutoSync();
        _autoSyncCts = new CancellationTokenSource();
        var ct = _autoSyncCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await SyncAsync(settings, ct);
                    onSync?.Invoke(result);

                    var delay = TimeSpan.FromMinutes(settings.SyncIntervalMinutes);
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AddLog(LogLevel.Error, $"Auto-sync error: {ex.Message}");
                    try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    public void StopAutoSync()
    {
        _autoSyncCts?.Cancel();
        _autoSyncCts?.Dispose();
        _autoSyncCts = null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ISAPI Tier Implementations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetch ALL AcsEvent records via paginated JSON requests.
    /// Pages through 200 records at a time until the device returns
    /// fewer records than maxResults (last page) or we hit 50 pages (10,000 records).
    /// </summary>
    private async Task<(List<ImportedPunch> Events, string Message)> TryAcsEventJson(
        string baseUrl, string fromStr, string toStr, CancellationToken ct)
    {
        var allEvents = new List<ImportedPunch>();
        const int pageSize = 200;
        const int maxPages = 50;
        int position = 0;
        int page = 0;
        bool morePages = true;

        try
        {
            while (morePages && page < maxPages)
            {
                var searchBody = JsonSerializer.Serialize(new
                {
                    AcsEventCond = new
                    {
                        searchID = "1",
                        searchResultPosition = position,
                        maxResults = pageSize,
                        major = 0,
                        minor = 0,
                        startTime = fromStr,
                        endTime = toStr
                    }
                });

                var resp = await _httpClient!.PostAsync(
                    $"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json",
                    new StringContent(searchBody, Encoding.UTF8, "application/json"), ct);

                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var pageEvents = HikvisionParser.ParseAcsEventJson(json);
                    allEvents.AddRange(pageEvents);
                    page++;

                    if (pageEvents.Count < pageSize)
                        morePages = false;
                    else
                        position += pageSize;

                    if (page > 1 && pageEvents.Count > 0)
                        AddLog(LogLevel.Info, $"  Page {page}: {pageEvents.Count} records (total: {allEvents.Count})");
                }
                else
                {
                    if (page == 0)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        return (new List<ImportedPunch>(), $"HTTP {(int)resp.StatusCode} — {Truncate(body, 100)}");
                    }
                    AddLog(LogLevel.Warning, $"  Page {page + 1} returned HTTP {(int)resp.StatusCode}. Stopping pagination.");
                    morePages = false;
                }
            }

            var paginationNote = page > 1 ? $" across {page} pages" : "";
            return (allEvents, $"{allEvents.Count} records{paginationNote}");
        }
        catch (Exception ex)
        {
            if (allEvents.Count > 0)
                return (allEvents, $"{allEvents.Count} records (pagination interrupted: {Truncate(ex.Message, 60)})");
            return (new List<ImportedPunch>(), $"Error: {ex.Message}");
        }
    }

    private async Task<(List<ImportedPunch> Events, string Message)> TryAcsEventXml(
        string baseUrl, string fromStr, string toStr, CancellationToken ct)
    {
        try
        {
            // Fix: Use the correct ISAPI XML format for AcsEvent search.
            // The version attribute and namespace must match what the device expects.
            // Some devices require major=5 for access control events.
            var xmlBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<AcsEventCond version=\"2.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">" +
                "<searchID>1</searchID>" +
                "<searchResultPosition>0</searchResultPosition>" +
                "<maxResults>200</maxResults>" +
                "<major>0</major><minor>0</minor>" +
                $"<startTime>{fromStr}</startTime>" +
                $"<endTime>{toStr}</endTime>" +
                "</AcsEventCond>";

            var resp = await _httpClient!.PostAsync(
                $"{baseUrl}/ISAPI/AccessControl/AcsEvent",
                new StringContent(xmlBody, Encoding.UTF8, "application/xml"), ct);

            if (resp.IsSuccessStatusCode)
            {
                var xml = await resp.Content.ReadAsStringAsync(ct);
                var events = HikvisionParser.ParseAcsEventXml(xml);
                return (events, $"{events.Count} records");
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            // If XML format is rejected (badJsonFormat), try without namespace
            if (body.Contains("badJsonFormat") || body.Contains("Invalid Format"))
            {
                AddLog(LogLevel.Info, "XML format rejected, trying simplified format...");
                var (retryEvents, retryMsg) = await TryAcsEventXmlSimplified(baseUrl, fromStr, toStr, ct);
                if (retryEvents.Count > 0) return (retryEvents, retryMsg);
            }
            return (new List<ImportedPunch>(), $"HTTP {(int)resp.StatusCode} — {Truncate(body, 100)}");
        }
        catch (Exception ex)
        {
            return (new List<ImportedPunch>(), $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Simplified XML format without namespace — some devices reject the
    /// namespaced format but accept a plain version.
    /// </summary>
    private async Task<(List<ImportedPunch> Events, string Message)> TryAcsEventXmlSimplified(
        string baseUrl, string fromStr, string toStr, CancellationToken ct)
    {
        try
        {
            var xmlBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<AcsEventCond version=\"2.0\">" +
                "<searchID>1</searchID>" +
                "<searchResultPosition>0</searchResultPosition>" +
                "<maxResults>200</maxResults>" +
                $"<startTime>{fromStr}</startTime>" +
                $"<endTime>{toStr}</endTime>" +
                "</AcsEventCond>";

            var resp = await _httpClient!.PostAsync(
                $"{baseUrl}/ISAPI/AccessControl/AcsEvent",
                new StringContent(xmlBody, Encoding.UTF8, "application/xml"), ct);

            if (resp.IsSuccessStatusCode)
            {
                var xml = await resp.Content.ReadAsStringAsync(ct);
                var events = HikvisionParser.ParseAcsEventXml(xml);
                return (events, $"{events.Count} records (simplified)");
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return (new List<ImportedPunch>(), $"HTTP {(int)resp.StatusCode} (simplified)");
        }
        catch (Exception ex)
        {
            return (new List<ImportedPunch>(), $"Error (simplified): {ex.Message}");
        }
    }

    private async Task<(List<ImportedPunch> Events, string Message)> TryAuditLogXml(
        string baseUrl, string fromStr, string toStr, CancellationToken ct)
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

            var resp = await _httpClient!.PostAsync(
                $"{baseUrl}/ISAPI/System/AuditLog",
                new StringContent(xmlBody, Encoding.UTF8, "application/xml"), ct);

            if (resp.IsSuccessStatusCode)
            {
                var xml = await resp.Content.ReadAsStringAsync(ct);
                var events = HikvisionParser.ParseAuditLogXml(xml);
                return (events, $"{events.Count} records");
            }

            return (new List<ImportedPunch>(), $"HTTP {(int)resp.StatusCode} (not supported)");
        }
        catch (Exception ex)
        {
            return (new List<ImportedPunch>(), $"Error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cloud Push
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(bool Success, string Message)> PushToCloud(
        SyncSettings settings, List<ImportedPunch> events, SyncResult result, CancellationToken ct)
    {
        try
        {
            // Validate API key before attempting push
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return (false, "Cloud API key is missing. Set your X-API-Key in Settings to enable cloud sync.");
            }

            var payload = JsonSerializer.Serialize(new
            {
                events = events,
                deviceInfo = new { name = DeviceName, model = DeviceModel, serial = SerialNo }
            });

            var cloudUrl = settings.CloudUrl!.TrimEnd('/') + "/api/access/save-imported";
            var request = new HttpRequestMessage(HttpMethod.Post, cloudUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-API-Key", settings.ApiKey);

            var cloudResp = await _httpClient!.SendAsync(request, ct);
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

                    result.EmployeesMatched = matched;
                    result.RecordsImported = imported;

                    return (true, $"Cloud sync: {fetched} events, {matched} matched, {imported} imported");
                }
                catch (JsonException)
                {
                    return (true, $"Cloud sync OK ({Truncate(cloudJson, 100)})");
                }
            }

            // Handle specific HTTP errors with helpful messages
            var errorDetail = cloudResp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "HTTP 401 — API key is invalid or missing. Check your X-API-Key in Settings.",
                HttpStatusCode.Forbidden => "HTTP 403 — Access denied. Your API key may not have permission for this operation.",
                HttpStatusCode.NotFound => "HTTP 404 — Cloud API endpoint not found. Check your Cloud URL in Settings.",
                _ => $"HTTP {(int)cloudResp.StatusCode} — {Truncate(cloudJson, 150)}"
            };
            return (false, errorDetail);
        }
        catch (Exception ex)
        {
            return (false, $"Cloud push failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private void AddLog(LogLevel level, string message)
    {
        var entry = new SyncLogEntry(level, message, DateTime.Now);
        _logHistory.Add(entry);
        if (_logHistory.Count > 500) _logHistory.RemoveAt(0);
        LogAdded?.Invoke(entry);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAutoSync();
        _httpClient?.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Supporting Types
// ═══════════════════════════════════════════════════════════════════════════

public enum LogLevel { Info, Success, Warning, Error }

public record SyncLogEntry(LogLevel Level, string Message, DateTime Timestamp);

public class SyncResult
{
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    // Device info
    public string DeviceName { get; set; } = "";
    public string DeviceModel { get; set; } = "";

    // Date range used
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }

    // Records
    public int RecordsFetched { get; set; }
    public List<ImportedPunch> Records { get; set; } = new();
    public int EmployeesMatched { get; set; }
    public int RecordsImported { get; set; }

    // Tier status
    public string Tier1Status { get; set; } = "Not attempted";
    public string Tier2Status { get; set; } = "Not attempted";
    public string Tier3Status { get; set; } = "Not attempted";

    // Cloud
    public string CloudStatus { get; set; } = "Not attempted";

    public TimeSpan Duration => CompletedAt - StartedAt;
}
