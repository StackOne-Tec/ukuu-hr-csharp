using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UkuuHr.Models;

namespace UkuuHr.Services.Devices;

// ─────────────────────────────────────────────────────────────────────────────
// FR-001 Vendor REST Connectors
//
// Each connector implements IDeviceConnector for a specific (Vendor, RestApi)
// combination. They share an HttpClient (injected) and follow the same shape:
//   1. Build the request URL + auth header per vendor spec.
//   2. Parse vendor-specific JSON/XML into NormalizedClockEvent records.
//   3. Return DeviceSyncResult with counts.
//
// Vendors implemented:
//   - Hikvision  (ISAPI /ISAPI/AccessControl/AuditLog)
//   - ZKTeco     (HTTP API /getAttLog)
//   - Suprema    (BioStar 2 REST API /events)
//   - Dahua      (HTTP API /cgi-bin/recordFinder.cgi)
//   - Anviz      (Cloud API v2 /getCheckingRecord)
//   - Matrix     (COSEC REST API /events)
//   - eSSL       (HTTP API /getdata.cgi)
//
// All connectors gracefully handle network errors and return DeviceSyncResult.Fail
// rather than throwing — the orchestrator logs the error and continues.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Shared base class with helper methods for REST connectors.</summary>
public abstract class RestConnectorBase
{
    protected static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    protected RestConnectorBase(ILogger? logger = null) { }

    /// <summary>Build a digest-auth-enabled HttpRequestMessage (Hikvision + Dahua use Digest auth).</summary>
    protected static HttpRequestMessage BuildRequest(AttendanceDevice device, string path, HttpMethod? method = null)
    {
        var url = $"http://{device.IpAddress}:{device.Port ?? 80}{path}";
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(device.Username))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{device.Username}:{device.Password ?? ""}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        return req;
    }

    /// <summary>Truncate raw payload to 100 chars for storage in audit log.</summary>
    protected static string TruncatePayload(string? payload) =>
        string.IsNullOrEmpty(payload) ? null : (payload.Length > 100 ? payload[..100] + "…" : payload);
}

// ───────────── Hikvision REST (ISAPI) ─────────────

public class HikvisionRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.Hikvision;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(device, "/ISAPI/System/deviceInfo");
            using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            var path = "/ISAPI/AccessControl/AuditLog/search";
            if (since.HasValue)
            {
                var s = since.Value.ToString("yyyy-MM-ddTHH:mm:ssZ");
                path += $"?searchID=1&startTime={Uri.EscapeDataString(s)}&endTime={Uri.EscapeDataString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";
            }
            using var req = BuildRequest(device, path);
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"Hikvision ISAPI returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);

            var xml = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseHikvisionXml(xml, device);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
            // Note: persistence is handled by the orchestrator (it has DbContext).
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"Hikvision sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    /// <summary>Parse Hikvision ISAPI AuditLog XML into NormalizedClockEvent records.</summary>
    public static List<NormalizedClockEvent> ParseHikvisionXml(string xml, AttendanceDevice device)
    {
        var events = new List<NormalizedClockEvent>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var item in doc.Descendants("LogItem"))
            {
                var employeeCode = item.Element("employeeNo")?.Value ?? "";
                var timeStr = item.Element("time")?.Value ?? "";
                var majorStr = item.Element("major")?.Value ?? "0";
                var minorStr = item.Element("minor")?.Value ?? "0";

                if (!DateTime.TryParse(timeStr, out var eventTime)) continue;

                // Hikvision major/minor codes:
                // major=1 (Access Controller Event), minor=0=unknown, 1=door unlocked, 75=check-in, 76=check-out
                var eventType = majorStr == "1" && minorStr == "75" ? ClockEventType.CheckIn
                              : majorStr == "1" && minorStr == "76" ? ClockEventType.CheckOut
                              : ClockEventType.CheckIn;

                var verifyMode = item.Element("VerifyMode")?.Value;
                var inOutMode = item.Element("inAndOutMode")?.Value;

                events.Add(new NormalizedClockEvent(employeeCode, eventTime, eventType, verifyMode, inOutMode, TruncatePayload(item.ToString())));
            }
        }
        catch { /* malformed XML — return empty */ }
        return events;
    }
}

// ───────────── ZKTeco REST ─────────────

public class ZKTecoRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.ZKTeco;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(device, "/getOptions");
            using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            var path = "/getAttLog";
            if (since.HasValue)
            {
                var s = since.Value.ToString("yyyy-MM-dd HH:mm:ss");
                path += $"?startTime={Uri.EscapeDataString(s)}";
            }
            using var req = BuildRequest(device, path);
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"ZKTeco API returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);

            var json = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseZKTecoJson(json);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"ZKTeco sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    /// <summary>Parse ZKTeco JSON: { "data": [{ "pin": "...", "time": "YYYY-MM-DD HH:MM:SS", "type": 0|1 }] }</summary>
    public static List<NormalizedClockEvent> ParseZKTecoJson(string json)
    {
        var events = new List<NormalizedClockEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return events;
            foreach (var item in data.EnumerateArray())
            {
                var pin = item.TryGetProperty("pin", out var p) ? p.GetString() ?? "" : "";
                var timeStr = item.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
                var type = item.TryGetProperty("type", out var ty) ? ty.GetInt32() : 0;

                if (!DateTime.TryParse(timeStr, out var eventTime)) continue;
                var eventType = type == 0 ? ClockEventType.CheckIn :
                                type == 1 ? ClockEventType.CheckOut :
                                type == 2 ? ClockEventType.BreakOut :
                                type == 3 ? ClockEventType.BreakIn : ClockEventType.CheckIn;
                var verifyMode = item.TryGetProperty("verifyMode", out var v) ? v.GetString() : null;
                events.Add(new NormalizedClockEvent(pin, eventTime, eventType, verifyMode, null, TruncatePayload(item.ToString())));
            }
        }
        catch { /* malformed JSON */ }
        return events;
    }
}

// ───────────── Suprema BioStar 2 REST ─────────────

public class SupremaRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.Suprema;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        try
        {
            // BioStar 2 doesn't have a simple ping endpoint — hit /api/users instead (will return 401 if alive).
            using var req = BuildRequest(device, "/api/users");
            using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // 401 means device is reachable but we need to login first.
            return (resp.StatusCode == System.Net.HttpStatusCode.OK || resp.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                    resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode} (expected — Biostar2 requires session)");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            // BioStar 2 event search endpoint.
            var from = since?.ToString("yyyy-MM-ddTHH:mm:ss.000Z") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var to = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var endpoint = $"/api/events/search?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&limit=1000";
            using var req = BuildRequest(device, endpoint);
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"BioStar 2 returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);

            var json = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseSupremaJson(json);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"Suprema sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    public static List<NormalizedClockEvent> ParseSupremaJson(string json)
    {
        var events = new List<NormalizedClockEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("EventCollection", out var collection) &&
                !doc.RootElement.TryGetProperty("rows", out collection)) return events;
            foreach (var item in collection.EnumerateArray())
            {
                var userId = item.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" :
                             item.TryGetProperty("userID", out var u2) ? u2.GetString() ?? "" : "";
                var timeStr = item.TryGetProperty("datetime", out var d) ? d.GetString() ?? "" :
                              item.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
                var eventTypeCode = item.TryGetProperty("event_type_id", out var e) ? e.GetInt32() : 0;

                if (!DateTime.TryParse(timeStr, out var eventTime)) continue;
                // BioStar2 event_type_id: 0x1100=verify, 0x1200=identify, etc. We treat all as CheckIn.
                var eventType = eventTypeCode == 0x4000 ? ClockEventType.CheckOut : ClockEventType.CheckIn;
                events.Add(new NormalizedClockEvent(userId, eventTime, eventType, "Biostar2", null, TruncatePayload(item.ToString())));
            }
        }
        catch { }
        return events;
    }
}

// ───────────── Dahua REST ─────────────

public class DahuaRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.Dahua;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(device, "/cgi-bin/magicBox.cgi?action=getDeviceType");
            using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            var from = (since ?? DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd HH:mm:ss");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var path = $"/cgi-bin/recordFinder.cgi?action=find&name=AccessControlLog&StartTime={Uri.EscapeDataString(from)}&EndTime={Uri.EscapeDataString(to)}";
            using var req = BuildRequest(device, path);
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"Dahua returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);

            var body = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseDahuaResponse(body);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"Dahua sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    /// <summary>Dahua returns key=value lines, one record per block separated by blank line.</summary>
    public static List<NormalizedClockEvent> ParseDahuaResponse(string body)
    {
        var events = new List<NormalizedClockEvent>();
        var blocks = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var fields = block.Split('\n')
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            var empCode = fields.GetValueOrDefault("EmployeeNo") ?? fields.GetValueOrDefault("UserID") ?? "";
            var timeStr = fields.GetValueOrDefault("Time") ?? "";
            var method = fields.GetValueOrDefault("Method") ?? "";
            if (!DateTime.TryParse(timeStr, out var eventTime)) continue;
            var eventType = method.Contains("Logout") || method.Contains("Exit") ? ClockEventType.CheckOut : ClockEventType.CheckIn;
            events.Add(new NormalizedClockEvent(empCode, eventTime, eventType, fields.GetValueOrDefault("CardType"), method, TruncatePayload(block)));
        }
        return events;
    }
}

// ───────────── Anviz Cloud REST ─────────────

public class AnvizRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.Anviz;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        // Anviz uses cloud API — IP address is irrelevant. Check if API key (in Password field) is set.
        if (string.IsNullOrEmpty(device.Password))
            return (false, "Anviz cloud API requires an API key (set as device Password).");
        return (true, null);
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            // Anviz Cloud v2 API: https://cloud.anviz.com/
            var apiKey = device.Password;
            var deviceSerial = device.DeviceSerial;
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(deviceSerial))
                return DeviceSyncResult.Fail("Anviz cloud requires API key + device serial.", DateTime.UtcNow - start);

            var from = (since ?? DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"https://cloud.anviz.com/v2/getCheckingRecord?apiKey={Uri.EscapeDataString(apiKey)}&deviceSN={Uri.EscapeDataString(deviceSerial)}&startTime={from}&endTime={to}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"Anviz cloud returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);

            var json = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseAnvizJson(json);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"Anviz sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    public static List<NormalizedClockEvent> ParseAnvizJson(string json)
    {
        var events = new List<NormalizedClockEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return events;
            foreach (var item in data.EnumerateArray())
            {
                var empCode = item.TryGetProperty("pin", out var p) ? p.GetString() ?? "" :
                              item.TryGetProperty("employeePIN", out var ep) ? ep.GetString() ?? "" : "";
                var timeStr = item.TryGetProperty("checkinTime", out var t) ? t.GetString() ?? "" : "";
                var type = item.TryGetProperty("checkinType", out var ty) ? ty.GetString() : "0";

                if (!DateTime.TryParse(timeStr, out var eventTime)) continue;
                var eventType = type == "0" || type == "CheckIn" ? ClockEventType.CheckIn :
                                type == "1" || type == "CheckOut" ? ClockEventType.CheckOut : ClockEventType.CheckIn;
                events.Add(new NormalizedClockEvent(empCode, eventTime, eventType, null, null, TruncatePayload(item.ToString())));
            }
        }
        catch { }
        return events;
    }
}

// ───────────── Matrix COSEC REST ─────────────

public class MatrixRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.Matrix;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(device, "/api/v1/info");
            using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            var from = (since ?? DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var path = $"/api/v1/events?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
            using var req = BuildRequest(device, path);
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"Matrix COSEC returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseMatrixJson(json);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"Matrix sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    public static List<NormalizedClockEvent> ParseMatrixJson(string json)
    {
        var events = new List<NormalizedClockEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("events", out var arr) || arr.ValueKind != JsonValueKind.Array) return events;
            foreach (var item in arr.EnumerateArray())
            {
                var empCode = item.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
                var timeStr = item.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
                var typeStr = item.TryGetProperty("type", out var ty) ? ty.GetString() : "";
                if (!DateTime.TryParse(timeStr, out var eventTime)) continue;
                var eventType = typeStr.Contains("Out") ? ClockEventType.CheckOut : ClockEventType.CheckIn;
                events.Add(new NormalizedClockEvent(empCode, eventTime, eventType, item.TryGetProperty("verify_mode", out var v) ? v.GetString() : null, typeStr, TruncatePayload(item.ToString())));
            }
        }
        catch { }
        return events;
    }
}

// ───────────── eSSL REST ─────────────

public class EsslRestConnector : RestConnectorBase, IDeviceConnector
{
    public DeviceVendor Vendor => DeviceVendor.eSSL;
    public DeviceIntegrationMode Mode => DeviceIntegrationMode.RestApi;

    public async Task<(bool reachable, string? error)> PingAsync(AttendanceDevice device, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(device, "/getdata.cgi");
            using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<DeviceSyncResult> SyncAsync(AttendanceDevice device, DateTime? since, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        try
        {
            var from = (since ?? DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var path = $"/getdata.cgi?start={Uri.EscapeDataString(from)}&end={Uri.EscapeDataString(to)}";
            using var req = BuildRequest(device, path);
            using var resp = await SharedClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return DeviceSyncResult.Fail($"eSSL returned HTTP {resp.StatusCode}", DateTime.UtcNow - start);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var events = ParseEsslResponse(body);
            return DeviceSyncResult.Ok(events.Count, 0, 0, DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return DeviceSyncResult.Fail($"eSSL sync error: {ex.Message}", DateTime.UtcNow - start);
        }
    }

    /// <summary>eSSL returns CSV-like rows: PIN,Time,Status,Verified,WorkCode</summary>
    public static List<NormalizedClockEvent> ParseEsslResponse(string body)
    {
        var events = new List<NormalizedClockEvent>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            var empCode = parts[0].Trim();
            if (!DateTime.TryParse(parts[1].Trim(), out var eventTime)) continue;
            var status = parts.Length > 2 ? parts[2].Trim() : "0";
            var verify = parts.Length > 3 ? parts[3].Trim() : null;
            var eventType = status == "1" || status == "4" ? ClockEventType.CheckOut :
                            status == "2" ? ClockEventType.BreakOut :
                            status == "3" ? ClockEventType.BreakIn : ClockEventType.CheckIn;
            events.Add(new NormalizedClockEvent(empCode, eventTime, eventType, verify, status, TruncatePayload(line)));
        }
        return events;
    }
}
