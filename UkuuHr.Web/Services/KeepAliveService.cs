using System.Net.Http.Headers;

namespace UkuuHr.Services;

/// <summary>
/// Background service that pings the app's own /health endpoint every 5 minutes.
/// This prevents Render's free-tier web services from spinning down after 15 min of inactivity,
/// keeping the app always warm and responsive.
/// Configure the public URL via the KEEP_ALIVE_URL env var (Render will set this automatically).
/// Falls back to localhost on the bound port for local development.
/// </summary>
public class KeepAliveService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostEnvironment _env;
    private readonly ILogger<KeepAliveService> _logger;

    private bool IsDevelopment => _env.IsDevelopment();

    public KeepAliveService(IHttpClientFactory httpClientFactory, IHostEnvironment env, ILogger<KeepAliveService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 30s for the app to fully start before the first ping
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var targetUrl = ResolveTargetUrl();
        _logger.LogInformation("KeepAlive service started — pinging {Url} every 5 minutes", targetUrl);

        using var http = _httpClientFactory.CreateClient("KeepAlive");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UkuuHr-KeepAlive/1.0");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await http.GetAsync($"{targetUrl}/health", stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("KeepAlive ping OK ({Status})", (int)response.StatusCode);
                }
                else
                {
                    if (IsDevelopment)
                        _logger.LogDebug("KeepAlive ping returned {Status}", (int)response.StatusCode);
                    else
                        _logger.LogWarning("KeepAlive ping returned {Status}", (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (IsDevelopment)
                    _logger.LogDebug(ex, "KeepAlive ping failed — will retry in 5 min");
                else
                    _logger.LogWarning(ex, "KeepAlive ping failed — will retry in 5 min");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private string ResolveTargetUrl()
    {
        // Prefer explicit KEEP_ALIVE_URL env var (set this to the public Render URL)
        var env = Environment.GetEnvironmentVariable("KEEP_ALIVE_URL");
        if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');

        // Next: RENDER_EXTERNAL_URL is set automatically by Render
        var render = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
        if (!string.IsNullOrWhiteSpace(render)) return render.TrimEnd('/');

        // Local development fallback
        var aspnetUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var port = Environment.GetEnvironmentVariable("PORT");
        if (string.IsNullOrWhiteSpace(port))
        {
            port = !string.IsNullOrWhiteSpace(aspnetUrls) && aspnetUrls.Contains("5000") ? "5000" : "8080";
        }
        return $"http://localhost:{port}";
    }
}
