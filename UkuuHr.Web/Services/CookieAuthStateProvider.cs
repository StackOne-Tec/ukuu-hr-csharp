using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace UkuuHr.Services;

/// <summary>
/// Custom AuthenticationStateProvider that wraps the HttpContext user.
/// </summary>
public class CookieAuthStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _http;
    public CookieAuthStateProvider(IHttpContextAccessor http) => _http = http;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = _http.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        return Task.FromResult(new AuthenticationState(user));
    }

    public void NotifyStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
