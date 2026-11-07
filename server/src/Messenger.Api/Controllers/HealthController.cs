using Messenger.Api.Contracts;
using Messenger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Api.Controllers;

/// <summary>API-10: liveness never touches the database (Neon may sleep); readiness runs SELECT 1.</summary>
[ApiController]
[AllowAnonymous]
[DisableRateLimiting]
[Route("health")]
[Tags("Health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Liveness")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public IActionResult Live() => Ok(new HealthResponse("Healthy"));

    [HttpGet("ready")]
    [EndpointSummary("Readiness (database reachable)")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ready([FromServices] MessengerDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return Ok(new HealthResponse("Healthy"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthResponse("Unhealthy"));
        }
    }
}
