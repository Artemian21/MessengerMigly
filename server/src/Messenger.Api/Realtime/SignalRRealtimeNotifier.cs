using Messenger.Application.Abstractions;
using Messenger.Infrastructure.Security;
using Microsoft.AspNetCore.SignalR;

namespace Messenger.Api.Realtime;

/// <summary>Server → client events. Messages and statuses are routed per user (all devices), not via groups.</summary>
public sealed class SignalRRealtimeNotifier(IHubContext<ChatHub> hub) : IRealtimeNotifier
{
    public Task MessageReceivedAsync(IReadOnlyCollection<Guid> userIds, object message, CancellationToken ct) =>
        Users(userIds).SendAsync("MessageReceived", message, ct);

    public Task MessageUpdatedAsync(IReadOnlyCollection<Guid> userIds, object message, CancellationToken ct) =>
        Users(userIds).SendAsync("MessageUpdated", message, ct);

    public Task MessageDeletedAsync(IReadOnlyCollection<Guid> userIds, Guid conversationId, Guid messageId, long sequence, long updatedSequence,
        CancellationToken ct) =>
        Users(userIds).SendAsync("MessageDeleted", new { conversationId, messageId, sequence, updatedSequence }, ct);

    public Task MessageStatusChangedAsync(Guid authorId, Guid conversationId, Guid messageId, string status, CancellationToken ct) =>
        hub.Clients.User(authorId.ToString()).SendAsync("MessageStatusChanged", new { conversationId, messageId, status }, ct);

    public Task ConversationUpdatedAsync(IReadOnlyCollection<Guid> userIds, Guid conversationId, CancellationToken ct) =>
        Users(userIds).SendAsync("ConversationUpdated", new { conversationId }, ct);

    public Task ConversationReadAsync(Guid userId, Guid conversationId, long lastReadSequence, CancellationToken ct) =>
        hub.Clients.User(userId.ToString()).SendAsync("ConversationRead", new { conversationId, lastReadSequence }, ct);

    private IClientProxy Users(IReadOnlyCollection<Guid> userIds) => hub.Clients.Users(userIds.Select(id => id.ToString()));
}

/// <summary>Clients.User(id) targets every connection of the user identified by the JWT `sub` claim.</summary>
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst(ClaimTypesShort.Sub)?.Value;
}
