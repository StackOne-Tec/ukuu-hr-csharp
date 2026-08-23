using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Transactional email via the Resend HTTP API (https://resend.com).
//
// Enabled by setting RESEND_API_KEY (+ optional RESEND_FROM). Without a key the
// service is a no-op — every send simply returns false, so callers can fire
// best-effort emails without environment guards. All sends are wrapped in
// try/catch and never throw.
// ─────────────────────────────────────────────────────────────────────────────
public class EmailService
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _from;
    private readonly ILogger<EmailService> _logger;
    private readonly bool _enabled;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _logger = logger;
        _apiKey = config["RESEND_API_KEY"];
        _from = config["RESEND_FROM"] ?? "Ukuu HR <noreply@ukuuhr.com>";
        _enabled = !string.IsNullOrWhiteSpace(_apiKey);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (_enabled)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public bool Enabled => _enabled;

    /// <summary>
    /// Best-effort HTML email. Returns true when Resend accepted the message.
    /// Never throws — email is always secondary to the business operation.
    /// </summary>
    public async Task<bool> SendAsync(string to, string subject, string htmlBody)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(to))
            return false;

        try
        {
            var payload = JsonSerializer.Serialize(new { from = _from, to, subject, html = htmlBody });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("https://api.resend.com/emails", content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("Resend rejected email to {To} (HTTP {Status}): {Body}",
                    to, (int)resp.StatusCode, body.Length > 300 ? body[..300] + "..." : body);
                return false;
            }
            _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email send to {To} failed", to);
            return false;
        }
    }

    /// <summary>Send to multiple recipients (one Resend call, comma-joined).</summary>
    public async Task<bool> SendToManyAsync(IEnumerable<string> tos, string subject, string htmlBody)
    {
        var joined = string.Join(",", tos.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
        if (string.IsNullOrEmpty(joined)) return false;
        return await SendAsync(joined, subject, htmlBody);
    }

    /// <summary>Simple branded text wrapper so emails share the product look.</summary>
    public static string WrapHtml(string title, string bodyHtml) =>
        $@"<div style=""font-family:'Segoe UI',Helvetica,Arial,sans-serif;max-width:560px;margin:0 auto;border:1px solid #E8E4F0;border-radius:14px;overflow:hidden;"">
  <div style=""background:#25163F;color:#FCFBFF;padding:18px 24px;"">
    <div style=""font-size:18px;font-weight:800;letter-spacing:.02em;"">Ukuu HR</div>
    <div style=""font-size:12px;opacity:.75;"">Modern HRMS for Africa</div>
  </div>
  <div style=""padding:24px;color:#25163F;font-size:14px;line-height:1.6;"">
    <h2 style=""margin:0 0 12px;font-size:17px;"">{title}</h2>
    {bodyHtml}
  </div>
  <div style=""padding:14px 24px;background:#F3F1F6;color:#6b6580;font-size:11px;"">
    Sent by Ukuu HR — you are receiving this because of your role or a subscription you hold.
  </div>
</div>";
}
