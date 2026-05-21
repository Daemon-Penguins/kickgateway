using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace TailoredApps.KickGateway.Api.Auth;

/// <summary>
/// Resolves the current user from the HTTP request that established the
/// Blazor Server circuit. Cookie auth is the only auth scheme so the
/// HttpContext.User is the source of truth; we mirror it into the
/// AuthenticationState the component tree sees.
/// </summary>
public sealed class BlazorAuthStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _http;

    public BlazorAuthStateProvider(IHttpContextAccessor http) { _http = http; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = _http.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        return Task.FromResult(new AuthenticationState(user));
    }
}
