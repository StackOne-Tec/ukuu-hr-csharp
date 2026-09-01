using System.Net.Http.Headers;

namespace UkuuHr.Services;

/// <summary>
/// A DelegatingHandler that copies the auth cookie (and all cookies) from the
/// current HttpContext to every outgoing HttpClient request. This is necessary
/// because in Blazor Server, the injected HttpClient is a separate HTTP client
/// that does NOT automatically share the user's browser cookies.
///
/// Without this handler, API calls from Blazor pages (e.g. SettingsApiKeys.razor
/// calling /api/api-keys/create) would be unauthenticated and fail with 401/403.
/// </summary>
public class HttpClientForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpClientForwardingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        InnerHandler = new HttpClientHandler();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx != null)
        {
            // Copy the Cookie header from the current HTTP request to the HttpClient request
            var cookieHeader = ctx.Request.Headers["Cookie"].ToString();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Add("Cookie", cookieHeader);
            }

            // Also copy the X-API-Key header if present (for API-key-authenticated requests)
            var apiKey = ctx.Request.Headers["X-API-Key"].ToString();
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("X-API-Key", apiKey);
            }

            // Copy the Authorization header if present
            var authHeader = ctx.Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(authHeader))
            {
                request.Headers.Add("Authorization", authHeader);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
