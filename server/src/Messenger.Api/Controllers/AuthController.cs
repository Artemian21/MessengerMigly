using MediatR;
using Messenger.Api.Contracts;
using Messenger.Api.Infrastructure;
using Messenger.Application.Auth;
using Messenger.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Messenger.Api.Controllers;

/// <summary>Registration, email verification, login, refresh rotation, password reset and sessions.</summary>
[Route("api/v1/auth")]
[Tags("Auth")]
public sealed class AuthController(ISender mediator) : ApiControllerBase(mediator)
{
    private const string Accepted202 = "If the address can receive email, a message is on its way.";

    /// <summary>Register a new account (AUTH-01). Always 202 for a known email, so accounts cannot be enumerated.</summary>
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [EndpointSummary("Register")]
    [EndpointDescription("Creates an account and sends a verification email. Returns 202 even if the email is already registered.")]
    [ProducesResponseType(typeof(AcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Register(RegisterRequest request, CancellationToken ct) =>
        Send(new RegisterCommand(request.Email, request.Password, request.Username, request.DisplayName),
            () => Accepted(new AcceptedResponse(ApiLocalizer.Translate("Check your inbox to confirm your email address."))), ct);

    [HttpPost("verify-email"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [EndpointSummary("Confirm email")]
    [EndpointDescription("Consumes the single-use token from the verification email. 400 = unknown token, 410 = used or expired.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public Task<IActionResult> VerifyEmail(TokenRequest request, CancellationToken ct) =>
        SendNoContent(new VerifyEmailCommand(request.Token), ct);

    [HttpPost("resend-verification"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [EndpointSummary("Resend verification email")]
    [ProducesResponseType(typeof(AcceptedResponse), StatusCodes.Status202Accepted)]
    public Task<IActionResult> ResendVerification(EmailRequest request, CancellationToken ct) =>
        Send(new ResendVerificationCommand(request.Email), () => Accepted(new AcceptedResponse(ApiLocalizer.Translate(Accepted202))), ct);

    /// <summary>AUTH-04: returns a short-lived access token and sets the refresh cookie.</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [EndpointSummary("Sign in")]
    [EndpointDescription("Wrong credentials → 401 with a generic message. Unverified email or suspended account → 403. Lockout → 429.")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await Mediator.Send(new LoginCommand(request.Email, request.Password), ct);
        return result.IsSuccess ? Issue(result.Value) : Problem(result.Error!);
    }

    /// <summary>AUTH-05: rotates the refresh cookie (reuse detection, grace window for parallel tabs).</summary>
    [HttpPost("refresh"), AllowAnonymous, RequireSameOrigin]
    [EndpointSummary("Refresh access token")]
    [EndpointDescription("Uses the HttpOnly `rt` cookie. Requires `X-Requested-With: XMLHttpRequest` and a same-origin request.")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var result = await Mediator.Send(new RefreshCommand(RefreshCookie.Read(Request) ?? string.Empty), ct);
        if (!result.IsSuccess)
        {
            RefreshCookie.Clear(Response);
            return Problem(result.Error!);
        }

        return Issue(result.Value);
    }

    [HttpPost("logout"), AllowAnonymous, RequireSameOrigin]
    [EndpointSummary("Sign out")]
    [EndpointDescription("Revokes the current session (refresh-token family) and clears the cookie.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await Mediator.Send(new LogoutCommand(RefreshCookie.Read(Request)), ct);
        RefreshCookie.Clear(Response);
        return NoContent();
    }

    [HttpPost("forgot-password"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [EndpointSummary("Request a password reset email")]
    [ProducesResponseType(typeof(AcceptedResponse), StatusCodes.Status202Accepted)]
    public Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken ct) =>
        Send(new ForgotPasswordCommand(request.Email), () => Accepted(new AcceptedResponse(ApiLocalizer.Translate(Accepted202))), ct);

    [HttpPost("reset-password"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [EndpointSummary("Reset password with emailed token")]
    [EndpointDescription("Sets a new password and revokes every session of the account (AUTH-07).")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct) =>
        SendNoContent(new ResetPasswordCommand(request.Token, request.NewPassword), ct);

    [HttpPost("change-password")]
    [EndpointSummary("Change password")]
    [EndpointDescription("Keeps the current session and revokes all others (AUTH-08). Wrong current password → 422.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        SendNoContent(new ChangePasswordCommand(request.CurrentPassword, request.NewPassword), ct);

    [HttpGet("sessions")]
    [EndpointSummary("List active sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<SessionDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> Sessions(CancellationToken ct) => Send(new ListSessionsQuery(), ct);

    [HttpDelete("sessions/{id:guid}")]
    [EndpointSummary("Revoke a session")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> RevokeSession(Guid id, CancellationToken ct) => SendNoContent(new RevokeSessionCommand(id), ct);

    [HttpPost("external/complete-profile")]
    [EndpointSummary("Choose a username after OAuth sign-up")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> CompleteProfile(CompleteProfileRequest request, CancellationToken ct) =>
        Send(new CompleteProfileCommand(request.Username, request.DisplayName), ct);

    private OkObjectResult Issue(AuthTokens tokens)
    {
        if (tokens.RefreshToken is not null)
        {
            RefreshCookie.Set(Response, tokens.RefreshToken, tokens.RefreshTokenExpiresAt!.Value);
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new AuthResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.User));
    }
}
