using MediatR;
using Messenger.Application.Summaries;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Controllers;

[Route("api/v1/ai")]
[Tags("AI")]
public sealed class AiController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpGet("config")]
    [EndpointSummary("AI features of this deployment")]
    [EndpointDescription("Whether the unread-message summary is switched on and how many unread messages it needs at least.")]
    [ProducesResponseType(typeof(AiConfigDto), StatusCodes.Status200OK)]
    public Task<IActionResult> Config(CancellationToken ct) => Send(new GetAiConfigQuery(), ct);
}
