using MediatR;
using Messenger.Api.Infrastructure;
using Messenger.Application.Conversations;
using Messenger.Application.Messages;
using Messenger.Application.Users;
using Messenger.Domain.Common;
using Messenger.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Messenger.Api.Realtime;

/// <summary>D-06: the hub carries events and ephemeral signals only; state changes go through REST.
/// Client → server: AckDelivered, MarkRead, Typing, OpenConversation/CloseConversation, WatchPresence.</summary>
[Authorize]
public sealed class ChatHub(ISender mediator, CurrentUser currentUser, PresenceTracker presence) : Hub
{
    public const string Path = "/hubs/chat";
    private static readonly TimeSpan TypingThrottle = TimeSpan.FromSeconds(2);

    private Guid UserId => Context.User!.GetGuid(ClaimTypesShort.Sub)!.Value;

    public override async Task OnConnectedAsync()
    {
        await presence.ConnectedAsync(UserId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnected(UserId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>STS-02: idempotent delivery acknowledgement.</summary>
    public async Task<int> AckDelivered(Guid[] messageIds) =>
        Unwrap(await Run(new AckDeliveredCommand(messageIds ?? [])));

    /// <summary>STS-03: called when the chat is open and the tab is visible.</summary>
    public async Task<long> MarkRead(Guid conversationId, long upToSequence) =>
        Unwrap(await Run(new MarkReadCommand(conversationId, upToSequence)));

    /// <summary>PRS-03: ephemeral, throttled per connection; sent to others who have the conversation open.</summary>
    public async Task Typing(Guid conversationId)
    {
        var key = "typing:" + conversationId;
        var now = DateTimeOffset.UtcNow;
        if (Context.Items.TryGetValue(key, out var last) && last is DateTimeOffset previous && now - previous < TypingThrottle)
        {
            return;
        }

        Context.Items[key] = now;
        if (!(await Run(new CheckMembershipQuery(conversationId))).IsSuccess)
        {
            throw new HubException("Conversation not found.");
        }

        await Clients.OthersInGroup(ConversationGroup(conversationId)).SendAsync("Typing", new { conversationId, userId = UserId });
    }

    public async Task OpenConversation(Guid conversationId)
    {
        if (!(await Run(new CheckMembershipQuery(conversationId))).IsSuccess)
        {
            throw new HubException("Conversation not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
    }

    public Task CloseConversation(Guid conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));

    /// <summary>PRS-04: only users who share a conversation with the caller can be watched (max 100).</summary>
    public async Task<IReadOnlyList<PresenceDto>> WatchPresence(Guid[] userIds)
    {
        var snapshot = Unwrap(await Run(new GetPresenceQuery(userIds ?? [])));
        foreach (var item in snapshot)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, PresenceGroup(item.UserId));
        }

        return snapshot;
    }

    public static string ConversationGroup(Guid conversationId) => $"conv:{conversationId}";

    public static string PresenceGroup(Guid userId) => $"presence:{userId}";

    private Task<TResult> Run<TResult>(IRequest<TResult> request)
    {
        currentUser.Set(Context.User!, Context.GetHttpContext());
        return mediator.Send(request, Context.ConnectionAborted);
    }

    private static T Unwrap<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new HubException(result.Error!.Message);
}
