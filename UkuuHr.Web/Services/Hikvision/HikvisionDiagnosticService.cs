using System.Net.Http.Headers;
using System.Text;
using UkuuHr.Models;

namespace UkuuHr.Services.Hikvision;

// ─────────────────────────────────────────────────────────────────────────────
// Hikvision ISAPI Diagnostic Probe Service
//
// Makes raw HTTP requests to Hikvision ISAPI endpoints and captures the full
// response (status code, headers, body) without throwing on error codes.
// Generates equivalent curl commands so users can test from the terminal.
//
// This is the definitive way to determine which ISAPI endpoints a specific
// device model supports — critical for devices like DS-K1T343EFWX that may
// not support ?format=json or certain endpoint paths.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Result of a single ISAPI endpoint probe.</summary>
public class IsapiProbeResult
{
    /// <summary>Human-readable name of the endpoint (e.g. "Device Info").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>ISAPI path (e.g. "/ISAPI/System/deviceInfo").</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>HTTP method (GET, POST, PUT).</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Full URL that was probed.</summary>
    public string FullUrl { get; set; } = string.Empty;

    /// <summary>HTTP status code returned (0 if connection failed).</summary>
    public int StatusCode { get; set; }

    /// <summary>HTTP status description (e.g. "OK", "Not Found").</summary>
    public string StatusDescription { get; set; } = string.Empty;

    /// <summary>Response body (truncated to first 2000 chars).</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>Time taken for the request in milliseconds.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Exception message if the request failed entirely (e.g. connection refused).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Equivalent curl command for terminal testing.</summary>
    public string CurlCommand { get; set; } = string.Empty;

    /// <summary>POST body XML if this was a POST request.</summary>
    public string? PostBody { get; set; }

    // ───── Helpers ─────

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    public bool IsFailed => StatusCode >= 400 || !string.IsNullOrEmpty(ErrorMessage);
    public string StatusIcon => IsSuccess ? "check_circle" : IsFailed ? "cancel" : "help";
    public string StatusColor => IsSuccess ? "var(--bk-success)" : IsFailed ? "var(--bk-error)" : "var(--bk-warning)";
    public string StatusBadge => IsSuccess ? "OK" : StatusCode > 0 ? $"{StatusCode}" : "FAIL";

    /// <summary>Category for grouping probes in the UI.</summary>
    public string Category { get; set; } = "General";
}

/// <summary>Full diagnostic report for a device.</summary>
public class IsapiDiagnosticReport
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public bool UseHttps { get; set; }
    public string Username { get; set; } = "admin";
    public DateTime ProbedAt { get; set; } = DateTime.UtcNow;
    public List<IsapiProbeResult> Probes { get; set; } = new();
    public string BaseUrl { get; set; } = string.Empty;

    // Summary counts
    public int TotalProbes => Probes.Count;
    public int SuccessCount => Probes.Count(p => p.IsSuccess);
    public int FailedCount => Probes.Count(p => p.IsFailed);
    public int UnsupportedCount => Probes.Count(p => p.StatusCode == 404 || p.StatusCode == 400);
}

/// <summary>
/// Diagnostic service that probes Hikvision ISAPI endpoints and generates
/// curl commands for terminal-based testing.
/// </summary>
public class HikvisionDiagnosticService
{
    private readonly ILogger<HikvisionDiagnosticService> _logger;

    public HikvisionDiagnosticService(ILogger<HikvisionDiagnosticService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run a comprehensive ISAPI endpoint probe against a Hikvision device.
    /// Tests all known endpoints and URL variations to determine exactly which
    /// ones the device supports.
    /// </summary>
    public async Task<IsapiDiagnosticReport> ProbeDeviceAsync(
        string ipAddress, int port, string username, string password,
        bool useHttps = false, int timeoutSeconds = 15, CancellationToken ct = default)
    {
        var scheme = useHttps ? "https" : "http";
        var baseUrl = $"{scheme}://{ipAddress}:{port}";
        var now = DateTime.UtcNow;

        var report = new IsapiDiagnosticReport
        {
            IpAddress = ipAddress,
            Port = port,
            UseHttps = useHttps,
            Username = username,
            BaseUrl = baseUrl,
            ProbedAt = now
        };

        // Create a dedicated HttpClient for probing (don't throw on error codes)
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            PreAuthenticate = true,
            Credentials = new System.Net.NetworkCredential(username, password)
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // ───── Define all probe endpoints ─────
        var probes = new List<(string Category, string Name, string Method, string Path, string? PostBody)>
        {
            // Core System
            ("System", "Device Info", "GET", "/ISAPI/System/deviceInfo", null),
            ("System", "Capabilities", "GET", "/ISAPI/System/capabilities", null),
            ("System", "Device Status (Health)", "GET", "/ISAPI/System/status?format=json", null),
            ("System", "Device Status (XML)", "GET", "/ISAPI/System/status", null),
            ("System", "Device Time", "GET", "/ISAPI/System/time", null),
            ("System", "Network Config", "GET", "/ISAPI/System/networkInterfaces", null),
            ("System", "Device Capacity", "GET", "/ISAPI/System/deviceCapacity", null),

            // Access Control
            ("Access Control", "AcsEvent (JSON)", "POST", "/ISAPI/AccessControl/AcsEvent?format=json",
                BuildAcsEventSearchXml(now.AddDays(-1), now)),
            ("Access Control", "AcsEvent (XML)", "POST", "/ISAPI/AccessControl/AcsEvent",
                BuildAcsEventSearchXml(now.AddDays(-1), now)),
            ("Access Control", "AuditLog Search", "GET",
                $"/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime={Uri.EscapeDataString(now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"))}&endTime={Uri.EscapeDataString(now.ToString("yyyy-MM-ddTHH:mm:ssZ"))}",
                null),
            ("Access Control", "AuditLog (no params)", "GET", "/ISAPI/AccessControl/AuditLog/search", null),
            ("Access Control", "Door Status", "GET", "/ISAPI/AccessControl/Door/status", null),

            // People Management
            ("People", "All Persons", "GET", "/ISAPI/AccessControl/UserInfo/Search?format=json", null),

            // Streaming / Event Notification
            ("Events", "Event Notification Caps", "GET", "/ISAPI/Event/notification/capabilities", null),

            // Security
            ("Security", "Security Caps", "GET", "/ISAPI/Security/capabilities", null),
        };

        // ───── Execute each probe ─────
        foreach (var (category, name, method, path, postBody) in probes)
        {
            var probe = new IsapiProbeResult
            {
                Name = name,
                Path = path,
                Method = method,
                Category = category,
                FullUrl = $"{baseUrl}{path}",
                PostBody = postBody
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                HttpResponseMessage? resp = null;
                if (method == "GET")
                {
                    resp = await client.GetAsync(probe.FullUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                else if (method == "POST" && postBody != null)
                {
                    using var content = new StringContent(postBody, Encoding.UTF8, "application/xml");
                    resp = await client.PostAsync(probe.FullUrl, content, ct);
                }
                else if (method == "PUT" && postBody != null)
                {
                    using var content = new StringContent(postBody, Encoding.UTF8, "application/xml");
                    resp = await client.PutAsync(probe.FullUrl, content, ct);
                }

                if (resp != null)
                {
                    probe.StatusCode = (int)resp.StatusCode;
                    probe.StatusDescription = resp.ReasonPhrase ?? resp.StatusCode.ToString();
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    probe.ResponseBody = body.Length > 2000 ? body[..2000] + "\n... [truncated]" : body;
                    resp.Dispose();
                }
            }
            catch (TaskCanceledException)
            {
                probe.ErrorMessage = "Request timed out";
                probe.StatusCode = 0;
            }
            catch (HttpRequestException ex)
            {
                probe.ErrorMessage = ex.Message;
                probe.StatusCode = (int?)ex.StatusCode ?? 0;
            }
            catch (Exception ex)
            {
                probe.ErrorMessage = ex.Message;
                probe.StatusCode = 0;
            }
            sw.Stop();
            probe.ElapsedMs = sw.ElapsedMilliseconds;

            // Generate equivalent curl command
            probe.CurlCommand = GenerateCurlCommand(probe, username, password);

            report.Probes.Add(probe);
        }

        // Try to extract device model from the Device Info probe
        var deviceInfoProbe = report.Probes.FirstOrDefault(p => p.Name == "Device Info" && p.IsSuccess);
        if (deviceInfoProbe != null)
        {
            report.DeviceModel = ExtractXmlValue(deviceInfoProbe.ResponseBody, "model")
                ?? ExtractXmlValue(deviceInfoProbe.ResponseBody, "deviceName")
                ?? "Unknown";
        }

        return report;
    }

    /// <summary>
    /// Run a single probe against a specific endpoint path.
    /// Useful for ad-hoc testing of a custom URL.
    /// </summary>
    public async Task<IsapiProbeResult> ProbeSingleEndpointAsync(
        string ipAddress, int port, string username, string password,
        string method, string path, string? postBody = null,
        bool useHttps = false, int timeoutSeconds = 15, CancellationToken ct = default)
    {
        var scheme = useHttps ? "https" : "http";
        var baseUrl = $"{scheme}://{ipAddress}:{port}";
        var fullUrl = $"{baseUrl}{path}";

        var probe = new IsapiProbeResult
        {
            Name = "Custom Probe",
            Path = path,
            Method = method,
            FullUrl = fullUrl,
            PostBody = postBody,
            Category = "Custom"
        };

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            PreAuthenticate = true,
            Credentials = new System.Net.NetworkCredential(username, password)
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            HttpResponseMessage? resp = null;
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                resp = await client.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && postBody != null)
            {
                using var content = new StringContent(postBody, Encoding.UTF8, "application/xml");
                resp = await client.PostAsync(fullUrl, content, ct);
            }

            if (resp != null)
            {
                probe.StatusCode = (int)resp.StatusCode;
                probe.StatusDescription = resp.ReasonPhrase ?? resp.StatusCode.ToString();
                var body = await resp.Content.ReadAsStringAsync(ct);
                probe.ResponseBody = body.Length > 2000 ? body[..2000] + "\n... [truncated]" : body;
                resp.Dispose();
            }
        }
        catch (TaskCanceledException) { probe.ErrorMessage = "Request timed out"; probe.StatusCode = 0; }
        catch (HttpRequestException ex) { probe.ErrorMessage = ex.Message; probe.StatusCode = (int?)ex.StatusCode ?? 0; }
        catch (Exception ex) { probe.ErrorMessage = ex.Message; probe.StatusCode = 0; }
        sw.Stop();
        probe.ElapsedMs = sw.ElapsedMilliseconds;
        probe.CurlCommand = GenerateCurlCommand(probe, username, password);

        return probe;
    }

    // ───── Curl Command Generation ─────

    /// <summary>
    /// Generate the equivalent curl command for a probe result.
    /// Uses --digest for Hikvision digest auth, includes all headers.
    /// </summary>
    private static string GenerateCurlCommand(IsapiProbeResult probe, string username, string password)
    {
        var sb = new StringBuilder();
        sb.Append("curl -v");

        // Digest auth (Hikvision default)
        sb.Append($" --digest -u '{username}:{password}'");

        // Method
        if (probe.Method != "GET")
            sb.Append($" -X {probe.Method}");

        // Accept headers (matches our HttpClient setup)
        sb.Append(" -H 'Accept: application/xml, application/json'");

        // POST body
        if (!string.IsNullOrEmpty(probe.PostBody))
        {
            // Escape single quotes in the XML body
            var escapedBody = probe.PostBody.Replace("'", "'\\''");
            sb.Append($" -H 'Content-Type: application/xml'");
            sb.Append($" -d '{escapedBody}'");
        }

        // URL
        sb.Append($" '{probe.FullUrl}'");

        return sb.ToString();
    }

    // ───── XML Helpers ─────

    private static string BuildAcsEventSearchXml(DateTime start, DateTime end)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEventSearchDescription>
    <searchID>diag_test</searchID>
    <searchResultPosition>0</searchResultPosition>
    <maxResults>5</maxResults>
    <major>1</major>
    <minor>0</minor>
    <startTime>{start:yyyy-MM-ddTHH:mm:ssZ}</startTime>
    <endTime>{end:yyyy-MM-ddTHH:mm:ssZ}</endTime>
</AcsEventSearchDescription>";
    }

    private static string? ExtractXmlValue(string xml, string tagName)
    {
        try
        {
            var startTag = $"<{tagName}>";
            var endTag = $"</{tagName}>";
            var startIdx = xml.IndexOf(startTag, StringComparison.Ordinal);
            if (startIdx < 0) return null;
            startIdx += startTag.Length;
            var endIdx = xml.IndexOf(endTag, startIdx, StringComparison.Ordinal);
            if (endIdx < 0) return null;
            return xml[startIdx..endIdx].Trim();
        }
        catch { return null; }
    }
}
