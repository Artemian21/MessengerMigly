using MediatR;
using Messenger.Api.Contracts;
using Messenger.Api.Infrastructure;
using Messenger.Application.Abstractions;
using Messenger.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Messenger.Api.Controllers;

/// <summary>AUTH-09: backend-driven OAuth authorization-code flow. Providers are enabled only when configured.</summary>
[Route("api/v1/auth/external")]
[Tags("Auth")]
public sealed class ExternalAuthController(ISender mediator, IAuthenticationSchemeProvider schemes, IAuthTokenRepository tokens,
    ITokenService tokenService, IClock clock) : ApiControllerBase(mediator)
{
    public const string ExternalScheme = "External";
    public static readonly string[] KnownProviders = ["Google", "GitHub"];

    [HttpGet("providers"), AllowAnonymous]
    [EndpointSummary("List enabled OAuth providers")]
    [ProducesResponseType(typeof(ProvidersResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Providers()
    {
        var enabled = new List<string>();
        foreach (var provider in KnownProviders)
        {
            if (await schemes.GetSchemeAsync(provider) is not null)
            {
                enabled.Add(provider);
            }
        }

        return Ok(new ProvidersResponse(enabled));
    }

    [HttpGet("{provider}/login"), AllowAnonymous]
    [EndpointSummary("Start OAuth sign-in")]
    [EndpointDescription("Redirects to the provider. `mode=link` links the provider to the account signed in via the refresh cookie.")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Login(string provider, [FromQuery] string? mode, CancellationToken ct)
    {
        var scheme = KnownProviders.FirstOrDefault(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (scheme is null || await schemes.GetSchemeAsync(scheme) is null)
        {
            return Problem(Domain.Common.Error.NotFound("Provider"));
        }

        var properties = new AuthenticationProperties { RedirectUri = "/api/v1/auth/external/complete" };
        properties.Items["provider"] = scheme;
        if (mode == "link")
        {
            var raw = RefreshCookie.Read(Request);
            var token = raw is null ? null : await tokens.FindRefreshTokenAsync(tokenService.Hash(raw), ct);
            if (token is null || !token.IsActive(clock.UtcNow))
            {
                return Redirect("/login?error=session_expired");
            }

            properties.Items["linkUserId"] = token.UserId.ToString();
        }

        return Challenge(properties, scheme);
    }

    [HttpGet("complete"), AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Complete(CancellationToken ct)
    {
        var auth = await HttpContext.AuthenticateAsync(ExternalScheme);
        await HttpContext.SignOutAsync(ExternalScheme);
        if (!auth.Succeeded || auth.Principal is null || auth.Properties?.Items.TryGetValue("provider", out var provider) != true || provider is null)
        {
            return Redirect("/login?error=external_failed");
        }

        var principal = auth.Principal;
        var identity = new ExternalIdentity(
            provider,
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            principal.FindFirstValue(ClaimTypes.Email),
            // Google asserts email_verified for the returned address; GitHub's primary email is not treated as verified.
            provider == "Google" && principal.FindFirstValue("email_verified") is not "false",
            principal.FindFirstValue(ClaimTypes.Name));
        if (identity.ProviderUserId.Length == 0)
        {
            return Redirect("/login?error=external_failed");
        }

        if (auth.Properties.Items.TryGetValue("linkUserId", out var linkUserId) && Guid.TryParse(linkUserId, out var userId))
        {
            var linked = await Mediator.Send(new LinkExternalLoginCommand(userId, identity), ct);
            return Redirect(linked.IsSuccess ? "/settings?linked=" + provider : "/settings?error=" + linked.Error!.Code);
        }

        var result = await Mediator.Send(new ExternalLoginCommand(identity), ct);
        if (!result.IsSuccess)
        {
            return Redirect("/login?error=" + result.Error!.Code);
        }

        RefreshCookie.Set(Response, result.Value.RefreshToken!, result.Value.RefreshTokenExpiresAt!.Value);
        return Redirect(result.Value.User.NeedsProfile ? "/auth/callback?onboarding=1" : "/auth/callback");
    }
}
