using System.Security.Claims;
using Messenger.Application.Abstractions;
using Messenger.Infrastructure.Security;

namespace Messenger.Api.Infrastructure;

/// <summary>Reads the caller from the JWT principal. SignalR hub invocations set the principal explicitly,
/// because a hub method runs in its own DI scope without a per-call HttpContext.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? _principal;
    private string? _ip;
    private string? _userAgent;

    private ClaimsPrincipal? Principal => _principal ?? accessor.HttpContext?.User;

    public Guid? UserId => Principal?.Identity?.IsAuthenticated == true ? Principal.GetGuid(ClaimTypesShort.Sub) : null;

    public Guid? SessionId => Principal?.GetGuid(ClaimTypesShort.Sid);

    public bool IsAdmin => Principal?.FindFirst(ClaimTypesShort.Role)?.Value == "Admin";

    public string? IpAddress => _ip ?? accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => _userAgent ?? accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    public void Set(ClaimsPrincipal principal, HttpContext? connectionContext)
    {
        _principal = principal;
        _ip = connectionContext?.Connection.RemoteIpAddress?.ToString();
        _userAgent = connectionContext?.Request.Headers.UserAgent.ToString();
    }
}
