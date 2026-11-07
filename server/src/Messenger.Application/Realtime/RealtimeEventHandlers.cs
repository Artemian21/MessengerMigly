using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Application.Messages;
using Messenger.Domain.Conversations;
using Messenger.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Messenger.Application.Realtime;

/// <summary>Post-commit fan-out (D-05/D-06). Failures here are logged by the dispatcher and never roll back data.</summary>
public sealed class RealtimeEventHandlers(IApplicationDbContext db, MessageReader reader, IRealtimeNotifier notifier)
    : INotificationHandler<DomainEventNotification<MessageSent>>,
      INotificationHandler<DomainEventNotification<MessageEdited>>,
      INotificationHandler<DomainEventNotification<MessageDeleted>>,
      INotificationHandler<DomainEventNotification<MessagesDelivered>>,
      INotificationHandler<DomainEventNotification<MessagesRead>>,
      INotificationHandler<DomainEventNotification<ConversationChanged>>
{
    public async Task Handle(DomainEventNotification<MessageSent> notification, CancellationToken ct)
    {
        var e = notification.Event;
        await FanOutAsync(e.MessageId, e.ConversationId, updated: false, ct);
    }

    public async Task Handle(DomainEventNotification<MessageEdited> notification, CancellationToken ct)
    {
        var e = notification.Event;
        await FanOutAsync(e.MessageId, e.ConversationId, updated: true, ct);
    }

    public async Task Handle(DomainEventNotification<MessageDeleted> notification, CancellationToken ct)
    {
        var e = notification.Event;
        var updatedSequence = await db.Messages.Where(m => m.Id == e.MessageId).Select(m => m.UpdatedSequence).FirstAsync(ct);
        var viewers = await ViewersAsync(e.ConversationId, e.Sequence, ct);
        await notifier.MessageDeletedAsync(viewers.Select(v => v.UserId).ToList(), e.ConversationId, e.MessageId, e.Sequence, updatedSequence, ct);
    }

    public async Task Handle(DomainEventNotification<MessagesDelivered> notification, CancellationToken ct) =>
        await NotifyAuthorsAsync(notification.Event.MessageIds, ct);

    public async Task Handle(DomainEventNotification<MessagesRead> notification, CancellationToken ct)
    {
        var e = notification.Event;
        var ids = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == e.ConversationId && m.Sequence > e.FromSequenceExclusive && m.Sequence <= e.UpToSequence
                        && m.SenderId != e.ReaderId && m.DeletedAt == null)
            .OrderByDescending(m => m.Sequence).Take(200)
            .Select(m => m.Id).ToListAsync(ct);
        await NotifyAuthorsAsync(ids, ct);
        await notifier.ConversationReadAsync(e.ReaderId, e.ConversationId, e.UpToSequence, ct);
    }

    public async Task Handle(DomainEventNotification<ConversationChanged> notification, CancellationToken ct)
    {
        var e = notification.Event;
        var members = await db.Participants.AsNoTracking()
            .Where(p => p.ConversationId == e.ConversationId && p.LeftAt == null)
            .Select(p => p.UserId).ToListAsync(ct);
        await notifier.ConversationUpdatedAsync(members.Concat(e.RemovedUserIds).Distinct().ToList(), e.ConversationId, ct);
    }

    /// <summary>Each viewer gets their own projection (status for the author, reply preview per visibility floor).</summary>
    private async Task FanOutAsync(Guid messageId, Guid conversationId, bool updated, CancellationToken ct)
    {
        var sequence = await db.Messages.Where(m => m.Id == messageId).Select(m => m.Sequence).FirstAsync(ct);
        var viewers = await ViewersAsync(conversationId, sequence, ct);
        foreach (var group in viewers.GroupBy(v => v.Floor))
        {
            var userIds = group.Select(v => v.UserId).ToList();
            var dto = (await reader.ReadAsync(db.Messages.Where(m => m.Id == messageId), Guid.Empty, group.Key, ct))[0];
            var senderId = dto.Sender.Id;
            var others = userIds.Where(id => id != senderId).ToList();
            if (others.Count > 0)
            {
                await Send(others, dto, updated, ct);
            }

            if (userIds.Contains(senderId))
            {
                var own = (await reader.ReadAsync(db.Messages.Where(m => m.Id == messageId), senderId, group.Key, ct))[0];
                await Send([senderId], own, updated, ct);
            }
        }
    }

    private Task Send(IReadOnlyCollection<Guid> userIds, MessageDto dto, bool updated, CancellationToken ct) =>
        updated ? notifier.MessageUpdatedAsync(userIds, dto, ct) : notifier.MessageReceivedAsync(userIds, dto, ct);

    private async Task<List<(Guid UserId, long Floor)>> ViewersAsync(Guid conversationId, long sequence, CancellationToken ct)
    {
        var rows = await db.Participants.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.LeftAt == null)
            .Select(p => new { p.UserId, p.VisibleFromSequence, p.ClearedUpToSequence })
            .ToListAsync(ct);
        return rows.Select(r => (r.UserId, Floor: Math.Max(r.VisibleFromSequence - 1, r.ClearedUpToSequence)))
            .Where(r => sequence > r.Floor)
            .ToList();
    }

    private async Task NotifyAuthorsAsync(IReadOnlyList<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        var messages = await db.Messages.AsNoTracking().Where(m => messageIds.Contains(m.Id))
            .Select(m => new { m.Id, m.SenderId, m.ConversationId }).ToListAsync(ct);
        var statuses = await reader.GetStatusesAsync(messageIds, ct);
        foreach (var m in messages)
        {
            var status = statuses.GetValueOrDefault(m.Id);
            if (status != MessageStatus.Sent)
            {
                await notifier.MessageStatusChangedAsync(m.SenderId, m.ConversationId, m.Id, status.ToString(), ct);
            }
        }
    }
}

/// <summary>NTF-01: Web Push only for recipients without an active connection; dead subscriptions are removed.
/// The payload names the sender but never contains the message body.</summary>
public sealed class PushOnMessageSent(IApplicationDbContext db, IPresenceTracker presence, IPushSender sender,
    IPushSubscriptionRepository subscriptions, IUnitOfWork unitOfWork, IClock clock, ILogger<PushOnMessageSent> logger)
    : INotificationHandler<DomainEventNotification<MessageSent>>
{
    public async Task Handle(DomainEventNotification<MessageSent> notification, CancellationToken ct)
    {
        if (!sender.IsEnabled)
        {
            return;
        }

        var e = notification.Event;
        var info = await (
            from c in db.Conversations
            join u in db.Users on e.SenderId equals u.Id
            where c.Id == e.ConversationId
            select new { c.Type, c.Title, u.DisplayName }).AsNoTracking().FirstAsync(ct);
        var recipients = await db.Participants.AsNoTracking()
            .Where(p => p.ConversationId == e.ConversationId && p.LeftAt == null && p.UserId != e.SenderId)
            .Select(p => p.UserId).ToListAsync(ct);
        var offline = recipients.Where(id => !presence.IsOnline(id)).ToList();
        if (offline.Count == 0)
        {
            return;
        }

        var title = info.Type == ConversationType.Group ? $"{info.DisplayName} in {info.Title}" : info.DisplayName;
        var payload = new PushPayload(title, "New message", $"/chats/{e.ConversationId}", e.ConversationId.ToString());
        var changed = false;
        foreach (var subscription in await subscriptions.GetForUsersAsync(offline, ct))
        {
            var result = await sender.SendAsync(subscription, payload, ct);
            if (result == PushResult.Gone)
            {
                subscriptions.Remove(subscription);
                changed = true;
            }
            else if (result == PushResult.Sent)
            {
                subscription.MarkSuccess(clock.UtcNow);
                changed = true;
            }
        }

        if (changed)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        logger.LogDebug("Web push attempted for {Count} offline recipients", offline.Count);
    }
}
