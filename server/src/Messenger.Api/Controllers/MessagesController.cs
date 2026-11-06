using MediatR;
using Messenger.Api.Contracts;
using Messenger.Application.Admin;
using Messenger.Application.Common;
using Messenger.Application.Messages;
using Messenger.Application.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Controllers;

[Route("api/v1/messages")]
[Tags("Messages")]
public sealed class MessagesController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpPatch("{id:guid}")]
    [EndpointSummary("Edit my message")]
    [EndpointDescription("Author only, within 48 hours. The previous text goes to the edit history (visible to the author only).")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Edit(Guid id, EditMessageRequest request, CancellationToken ct) =>
        Send(new EditMessageCommand(id, request.Body), ct);

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete my message")]
    [EndpointDescription("Tombstone: everyone sees '[Message deleted]'; the text and edit history are erased; replies keep working.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => SendNoContent(new DeleteMessageCommand(id), ct);

    [HttpGet("{id:guid}/history")]
    [EndpointSummary("Edit history (author only)")]
    [ProducesResponseType(typeof(IReadOnlyList<EditHistoryEntryDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> History(Guid id, CancellationToken ct) => Send(new GetMessageHistoryQuery(id), ct);

    [HttpGet("{id:guid}/info")]
    [EndpointSummary("Delivery and read info per recipient (author only)")]
    [ProducesResponseType(typeof(MessageInfoDto), StatusCodes.Status200OK)]
    public Task<IActionResult> Info(Guid id, CancellationToken ct) => Send(new GetMessageInfoQuery(id), ct);

    [HttpPost("delivered")]
    [EndpointSummary("Acknowledge delivery (REST fallback for the hub)")]
    [ProducesResponseType(typeof(AckDeliveredResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Delivered(AckDeliveredRequest request, CancellationToken ct) =>
        Send(new AckDeliveredCommand(request.MessageIds), count => Ok(new AckDeliveredResponse(count)), ct);
}

[Route("api/v1/search")]
[Tags("Search")]
public sealed class SearchController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpGet("messages")]
    [EndpointSummary("Search my messages")]
    [EndpointDescription("Case-insensitive text search across conversations you can see (respects join/clear watermarks; deleted messages excluded). Returns {items,total,next}.")]
    [ProducesResponseType(typeof(PagedResult<SearchHitDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Messages([FromQuery] string? q, [FromQuery] Guid? conversationId, [FromQuery] int? limit, [FromQuery] int? offset,
        CancellationToken ct) => Send(new SearchMessagesQuery(q, conversationId, limit, offset), ct);
}

[Route("api/v1/push")]
[Tags("Push")]
public sealed class PushController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpGet("config"), AllowAnonymous]
    [EndpointSummary("Web Push availability and VAPID public key")]
    [ProducesResponseType(typeof(PushConfigDto), StatusCodes.Status200OK)]
    public Task<IActionResult> Config(CancellationToken ct) => Send(new GetPushConfigQuery(), ct);

    [HttpPost("subscriptions")]
    [EndpointSummary("Subscribe this browser to push notifications")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Subscribe(PushSubscriptionRequest request, CancellationToken ct) =>
        SendNoContent(new SubscribePushCommand(request.Endpoint, request.Keys.P256dh, request.Keys.Auth), ct);

    [HttpDelete("subscriptions")]
    [EndpointSummary("Unsubscribe this browser")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Unsubscribe([FromBody] UnsubscribePushRequest request, CancellationToken ct) =>
        SendNoContent(new UnsubscribePushCommand(request.Endpoint), ct);
}

[Route("api/v1/admin")]
[Tags("Admin")]
public sealed class AdminController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpGet("users")]
    [EndpointSummary("List users (system admin)")]
    [EndpointDescription("limit/offset pagination with total and next. Admins never see message content.")]
    [ProducesResponseType(typeof(PagedResult<AdminUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Users([FromQuery] string? q, [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct) =>
        Send(new AdminListUsersQuery(q, limit, offset), ct);

    [HttpPost("users/{id:guid}/suspend")]
    [EndpointSummary("Suspend a user (revokes sessions)")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Suspend(Guid id, CancellationToken ct) => SendNoContent(new SuspendUserCommand(id), ct);

    [HttpPost("users/{id:guid}/unsuspend")]
    [EndpointSummary("Unsuspend a user")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Unsuspend(Guid id, CancellationToken ct) => SendNoContent(new UnsuspendUserCommand(id), ct);

    [HttpGet("audit")]
    [EndpointSummary("Audit log")]
    [ProducesResponseType(typeof(PagedResult<AuditLogDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> Audit([FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct) =>
        Send(new ListAuditLogQuery(limit, offset), ct);
}
