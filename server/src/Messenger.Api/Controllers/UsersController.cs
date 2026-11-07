using MediatR;
using Messenger.Api.Contracts;
using Messenger.Application.Common;
using Messenger.Application.Users;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Controllers;

[Route("api/v1/users")]
[Tags("Users")]
public sealed class UsersController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpGet("me")]
    [EndpointSummary("Get my profile")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    public Task<IActionResult> Me(CancellationToken ct) => Send(new GetMeQuery(), ct);

    [HttpPatch("me")]
    [EndpointSummary("Update my display name")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> UpdateMe(UpdateProfileRequest request, CancellationToken ct) =>
        Send(new UpdateProfileCommand(request.DisplayName), ct);

    [HttpPut("me/username")]
    [EndpointSummary("Change my username")]
    [EndpointDescription("Usernames are unique case-insensitively: 3-32 characters of a-z, 0-9, '_' and '.'. Taken → 409.")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> ChangeUsername(ChangeUsernameRequest request, CancellationToken ct) =>
        Send(new ChangeUsernameCommand(request.Username), ct);

    [HttpDelete("me")]
    [EndpointSummary("Delete my account")]
    [EndpointDescription("Irreversible soft delete: frees the username, anonymizes the email, revokes sessions, keeps messages under 'Deleted account'.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> DeleteMe([FromBody] DeleteAccountRequest request, CancellationToken ct) =>
        SendNoContent(new DeleteAccountCommand(request.Password), ct);

    [HttpGet]
    [EndpointSummary("Search users")]
    [EndpointDescription("Prefix search by username or display name (min 2 characters). Returns {items,total,next}.")]
    [ProducesResponseType(typeof(PagedResult<UserSummaryDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct) =>
        Send(new SearchUsersQuery(q, limit, offset), ct);

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a public profile")]
    [EndpointDescription("Presence (online / last seen) is included only if you share a conversation.")]
    [ProducesResponseType(typeof(PublicUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Send(new GetUserQuery(id), ct);
}
