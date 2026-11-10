using MediatR;
using Messenger.Api.Contracts;
using Messenger.Api.Infrastructure;
using Messenger.Application.Common;
using Messenger.Application.Conversations;
using Messenger.Application.Messages;
using Messenger.Application.Summaries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Messenger.Api.Controllers;

[Route("api/v1/conversations")]
[Tags("Conversations")]
public sealed class ConversationsController(ISender mediator) : ApiControllerBase(mediator)
{
    [HttpGet]
    [EndpointSummary("List my conversations")]
    [EndpointDescription("Sorted by last activity, with unread count, last message preview and peer presence.")]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> List(CancellationToken ct) => Send(new ListConversationsQuery(), ct);

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a conversation")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Send(new GetConversationQuery(id), ct);

    [HttpGet("direct/{peerUserId:guid}")]
    [EndpointSummary("Find my personal chat with a user")]
    [EndpointDescription("404 means there is no chat yet: the client shows a draft until the first message is sent.")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetDirect(Guid peerUserId, CancellationToken ct) => Send(new GetDirectByPeerQuery(peerUserId), ct);

    [HttpPost("direct/{peerUserId:guid}/messages"), EnableRateLimiting(RateLimitPolicies.SendMessage)]
    [EndpointSummary("Send a personal message (creates the chat on first message)")]
    [EndpointDescription("Get-or-create of the one chat per pair, then send. Idempotent by clientMessageId: a retry returns 200 with the stored message.")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> SendDirect(Guid peerUserId, SendMessageRequest request, CancellationToken ct) =>
        Send(new SendDirectMessageCommand(peerUserId, request.ClientMessageId, request.Body, request.ReplyToMessageId, request.Attachments),
            SentResult, ct);

    [HttpPost("groups")]
    [EndpointSummary("Create a group")]
    [EndpointDescription("Groups are created with their members (at least 3 people including you). You become the Owner.")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> CreateGroup(CreateGroupRequest request, CancellationToken ct) =>
        Send(new CreateGroupCommand(request.Title, request.MemberIds), c => Created($"/api/v1/conversations/{c.Id}", c), ct);

    [HttpPatch("{id:guid}")]
    [EndpointSummary("Rename a group (Owner/Admin)")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Rename(Guid id, RenameGroupRequest request, CancellationToken ct) =>
        Send(new RenameGroupCommand(id, request.Title), ct);

    [HttpPost("{id:guid}/clear")]
    [EndpointSummary("Delete a personal chat for me")]
    [EndpointDescription("Moves my visibility watermark; the other person keeps everything. A new message brings the chat back.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Clear(Guid id, CancellationToken ct) => SendNoContent(new ClearConversationCommand(id), ct);

    [HttpPost("{id:guid}/leave")]
    [EndpointSummary("Leave a group")]
    [EndpointDescription("History stays for the others. If the owner leaves, the oldest admin (or member) becomes owner.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Leave(Guid id, CancellationToken ct) => SendNoContent(new LeaveConversationCommand(id), ct);

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete a group (Owner)")]
    [EndpointDescription("Soft delete: history remains readable, new messages and changes are rejected.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => SendNoContent(new DeleteGroupCommand(id), ct);

    [HttpGet("{id:guid}/members")]
    [EndpointSummary("List group members")]
    [ProducesResponseType(typeof(IReadOnlyList<MemberDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> Members(Guid id, CancellationToken ct) => Send(new ListMembersQuery(id), ct);

    [HttpPost("{id:guid}/members")]
    [EndpointSummary("Add members (Owner/Admin)")]
    [EndpointDescription("New members only see messages sent after they joined.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> AddMembers(Guid id, AddMembersRequest request, CancellationToken ct) =>
        SendNoContent(new AddMembersCommand(id, request.UserIds), ct);

    [HttpPatch("{id:guid}/members/{userId:guid}")]
    [EndpointSummary("Change a member's role (Owner)")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> ChangeRole(Guid id, Guid userId, ChangeRoleRequest request, CancellationToken ct) =>
        SendNoContent(new ChangeMemberRoleCommand(id, userId, request.Role), ct);

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [EndpointSummary("Remove a member")]
    [EndpointDescription("Owner removes anyone; admins remove plain members. Removing yourself = leaving.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct) =>
        SendNoContent(new RemoveMemberCommand(id, userId), ct);

    [HttpGet("{id:guid}/messages")]
    [EndpointSummary("Message history (cursor pagination)")]
    [EndpointDescription("Newest page by default. `before=<sequence>` loads older messages; `after=<sequence>` returns messages created or changed after that point (resync). Max 50 per page.")]
    [ProducesResponseType(typeof(MessagePageDto), StatusCodes.Status200OK)]
    public Task<IActionResult> Messages(Guid id, [FromQuery] long? before, [FromQuery] long? after, [FromQuery] int? limit, CancellationToken ct) =>
        Send(new GetMessagesQuery(id, before, after, limit), ct);

    [HttpPost("{id:guid}/messages"), EnableRateLimiting(RateLimitPolicies.SendMessage)]
    [EndpointSummary("Send a message")]
    [EndpointDescription("Body is trimmed and NFC-normalized; empty, >4000 characters or control characters → 422. Idempotent by clientMessageId.")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> SendMessage(Guid id, SendMessageRequest request, CancellationToken ct) =>
        Send(new SendMessageCommand(id, request.ClientMessageId, request.Body, request.ReplyToMessageId, request.Attachments), SentResult, ct);

    [HttpPost("{id:guid}/read")]
    [EndpointSummary("Mark messages as read (REST fallback for the hub)")]
    [ProducesResponseType(typeof(MarkReadResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> MarkRead(Guid id, MarkReadRequest request, CancellationToken ct) =>
        Send(new MarkReadCommand(id, request.UpToSequence), seq => Ok(new MarkReadResponse(seq)), ct);

    [HttpGet("{id:guid}/statuses")]
    [EndpointSummary("Statuses of my messages (resync)")]
    [ProducesResponseType(typeof(IReadOnlyList<MessageStatusDto>), StatusCodes.Status200OK)]
    public Task<IActionResult> Statuses(Guid id, [FromQuery] long fromSequence, CancellationToken ct) =>
        Send(new GetStatusesQuery(id, fromSequence), ct);

    [HttpPost("{id:guid}/summary"), EnableRateLimiting(RateLimitPolicies.Summary)]
    [EndpointSummary("Summarize what I have not read (\"що я пропустив\")")]
    [EndpointDescription("Starts a background summary of my unread messages (latest 300, my watermarks apply) and returns 202 with a jobId; the result arrives as the SummaryCompleted hub event and via GET. 200 with status \"empty\" when fewer than 2 messages are unread (Ai:MinMessages); an already running job is returned instead of a new one. `afterSequence` (optional) summarizes messages after that sequence instead of after my read position; the join/clear watermarks still apply. 404 when the AI summary is switched off.")]
    [ProducesResponseType(typeof(SummaryJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(SummaryJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> StartSummary(Guid id, [FromQuery] long? afterSequence, CancellationToken ct) =>
        Send(new StartSummaryQuery(id, afterSequence), job => job.Status == "empty" ? Ok(job) : Accepted(job), ct);

    [HttpGet("{id:guid}/summary/{jobId:guid}")]
    [EndpointSummary("Get a summary job")]
    [EndpointDescription("status is pending, ready or failed (with errorCode). Jobs are kept for 15 minutes and are visible to their owner only.")]
    [ProducesResponseType(typeof(SummaryJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetSummary(Guid id, Guid jobId, CancellationToken ct) => Send(new GetSummaryQuery(id, jobId), ct);

    private IActionResult SentResult(SendMessageResult result) =>
        result.Created
            ? Created($"/api/v1/messages/{result.Message.Id}", result.Message)
            : Ok(result.Message);
}
