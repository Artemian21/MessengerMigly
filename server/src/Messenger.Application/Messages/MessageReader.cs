using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Messages;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Messages;

/// <summary>Read-side projection of messages for one viewer (no tracking). Shared by history, sync and events.</summary>
public sealed class MessageReader(IApplicationDbContext db)
{
    public const int PreviewLength = 120;

    /// <summary>Projects <paramref name="query"/> (already filtered for visibility) into DTOs ordered by sequence.</summary>
    public async Task<IReadOnlyList<MessageDto>> ReadAsync(IQueryable<Message> query, Guid viewerId, long viewerFloor, CancellationToken ct)
    {
        var rows = await (
            from m in query
            join u in db.Users on m.SenderId equals u.Id
            select new
            {
                m.Id, m.ConversationId, m.Sequence, m.UpdatedSequence, m.SenderId,
                m.SenderUsernameSnapshot, m.SenderDisplayNameSnapshot,
                u.Username, u.DisplayName, u.Status,
                m.Body, m.ReplyToMessageId, m.ClientMessageId, m.CreatedAt, m.EditedAt, m.DeletedAt,
            }).AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var replyIds = rows.Where(r => r.ReplyToMessageId is not null).Select(r => r.ReplyToMessageId!.Value).Distinct().ToList();

        var replies = replyIds.Count == 0
            ? new Dictionary<Guid, ReplyPreviewDto>()
            : (await (
                from m in db.Messages
                join u in db.Users on m.SenderId equals u.Id
                where replyIds.Contains(m.Id)
                select new { m.Id, m.Sequence, m.Body, m.DeletedAt, u.DisplayName, u.Status, m.SenderDisplayNameSnapshot })
                .AsNoTracking().ToListAsync(ct))
            .ToDictionary(r => r.Id, r => new ReplyPreviewDto(
                r.Id,
                r.Sequence,
                r.Status == UserStatus.Deleted ? r.SenderDisplayNameSnapshot : r.DisplayName,
                r.DeletedAt is not null ? Message.DeletedPlaceholder
                    : r.Sequence > viewerFloor ? Preview(r.Body) : null,
                r.DeletedAt is not null));

        var attachments = (await db.MessageAttachments.AsNoTracking()
                .Where(a => ids.Contains(a.MessageId))
                .Select(a => new { a.MessageId, Dto = new AttachmentDto(a.Id, a.FileName, a.SizeBytes, a.ContentType) })
                .ToListAsync(ct))
            .ToLookup(a => a.MessageId, a => a.Dto);

        var ownIds = rows.Where(r => r.SenderId == viewerId).Select(r => r.Id).ToList();
        var statuses = await GetStatusesAsync(ownIds, ct);

        return rows
            .OrderBy(r => r.Sequence)
            .Select(r => new MessageDto(
                r.Id,
                r.ConversationId,
                r.Sequence,
                r.UpdatedSequence,
                Sender(r.SenderId, r.Username, r.DisplayName, r.Status, r.SenderUsernameSnapshot, r.SenderDisplayNameSnapshot),
                r.DeletedAt is not null ? Message.DeletedPlaceholder : r.Body,
                r.DeletedAt is not null,
                r.EditedAt is not null,
                r.ReplyToMessageId is { } replyId ? replies.GetValueOrDefault(replyId) : null,
                r.ClientMessageId,
                r.CreatedAt,
                r.EditedAt,
                r.DeletedAt,
                attachments[r.Id].ToList(),
                statuses.TryGetValue(r.Id, out var status) ? status.ToString() : null))
            .ToList();
    }

    /// <summary>STS-05: aggregate status over current recipients (active participants who could see the message).</summary>
    public async Task<Dictionary<Guid, MessageStatus>> GetStatusesAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        var counts = await (
            from m in db.Messages
            where messageIds.Contains(m.Id)
            select new
            {
                m.Id,
                Recipients = db.Participants.Count(p => p.ConversationId == m.ConversationId && p.LeftAt == null
                                                        && p.UserId != m.SenderId && p.VisibleFromSequence <= m.Sequence),
                Delivered = (
                    from r in db.MessageReceipts
                    join p in db.Participants on new { m.ConversationId, r.UserId } equals new { p.ConversationId, p.UserId }
                    where r.MessageId == m.Id && p.LeftAt == null && p.VisibleFromSequence <= m.Sequence
                    select r).Count(),
                Read = (
                    from r in db.MessageReceipts
                    join p in db.Participants on new { m.ConversationId, r.UserId } equals new { p.ConversationId, p.UserId }
                    where r.MessageId == m.Id && r.ReadAt != null && p.LeftAt == null && p.VisibleFromSequence <= m.Sequence
                    select r).Count(),
            }).AsNoTracking().ToListAsync(ct);

        return counts.ToDictionary(c => c.Id, c => MessageStatusRules.Aggregate(c.Recipients, c.Delivered, c.Read));
    }

    public static SenderDto Sender(Guid id, string username, string displayName, UserStatus status, string usernameSnapshot, string displayNameSnapshot) =>
        status == UserStatus.Deleted
            ? new SenderDto(id, usernameSnapshot, displayNameSnapshot, true)
            : new SenderDto(id, username, displayName, false);

    public static string? Preview(string? body) =>
        body is null ? null : body.Length <= PreviewLength ? body : body[..PreviewLength] + "…";
}
