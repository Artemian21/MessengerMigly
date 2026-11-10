using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Application.Messages;
using Messenger.Application.Users;
using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Conversations;

/// <summary>Builds conversation DTOs for one viewer: unread counts in one grouped query, last visible message, peer.</summary>
public sealed class ConversationReader(IApplicationDbContext db, IPresenceTracker presence)
{
    public async Task<IReadOnlyList<ConversationDto>> ReadAsync(Guid viewerId, Guid? onlyConversationId, CancellationToken ct)
    {
        var rows = await (
            from p in db.Participants
            join c in db.Conversations on p.ConversationId equals c.Id
            where p.UserId == viewerId && p.LeftAt == null
                  && (onlyConversationId == null || c.Id == onlyConversationId)
            let floor = p.VisibleFromSequence - 1 > p.ClearedUpToSequence ? p.VisibleFromSequence - 1 : p.ClearedUpToSequence
            select new
            {
                c.Id, c.Type, c.Title, c.LastSequence, c.LastMessageAt, c.CreatedAt, c.DeletedAt,
                p.Role, p.LastReadSequence, p.VisibleFromSequence, Floor = floor,
                MemberCount = db.Participants.Count(o => o.ConversationId == c.Id && o.LeftAt == null),
                Unread = db.Messages.Count(m => m.ConversationId == c.Id && m.Sequence > floor
                                                && m.Sequence > p.LastReadSequence && m.SenderId != viewerId && m.DeletedAt == null),
                Last = db.Messages
                    .Where(m => m.ConversationId == c.Id && m.Sequence > floor)
                    .OrderByDescending(m => m.Sequence)
                    .Select(m => new { m.Id, m.Sequence, m.SenderId, m.Body, m.DeletedAt, m.CreatedAt, m.SenderDisplayNameSnapshot })
                    .FirstOrDefault(),
                PeerId = c.Type == ConversationType.Direct
                    ? db.Participants.Where(o => o.ConversationId == c.Id && o.UserId != viewerId).Select(o => (Guid?)o.UserId).FirstOrDefault()
                    : null,
            }).AsNoTracking().ToListAsync(ct);

        // CNV-03: a cleared personal chat stays hidden until a new message arrives.
        rows = rows.Where(r => onlyConversationId != null || r.Type == ConversationType.Group || r.LastSequence > r.Floor).ToList();

        var userIds = rows.Where(r => r.PeerId is not null).Select(r => r.PeerId!.Value)
            .Concat(rows.Where(r => r.Last is not null).Select(r => r.Last!.SenderId))
            .Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        return rows
            .Select(r => new ConversationDto(
                r.Id,
                r.Type.ToString(),
                r.Title,
                r.PeerId is { } peerId && users.TryGetValue(peerId, out var peer) ? PresenceRules.ToPublic(peer, true, presence) : null,
                r.Role.ToString(),
                r.MemberCount,
                r.LastSequence,
                r.LastReadSequence,
                r.VisibleFromSequence,
                r.Unread,
                r.Last is null ? null : new MessagePreviewDto(
                    r.Last.Id, r.Last.Sequence, r.Last.SenderId,
                    users.TryGetValue(r.Last.SenderId, out var sender) && sender.Status != UserStatus.Deleted
                        ? sender.DisplayName : r.Last.SenderDisplayNameSnapshot,
                    r.Last.DeletedAt is not null ? Domain.Messages.Message.DeletedPlaceholder : MessageReader.Preview(r.Last.Body),
                    r.Last.DeletedAt is not null,
                    r.Last.CreatedAt),
                r.LastMessageAt,
                r.CreatedAt,
                r.DeletedAt is not null))
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToList();
    }
}

// CNV-09
public sealed record ListConversationsQuery : IQuery<Result<IReadOnlyList<ConversationDto>>>;

public sealed class ListConversationsHandler(ConversationReader reader, ICurrentUser currentUser)
    : IRequestHandler<ListConversationsQuery, Result<IReadOnlyList<ConversationDto>>>
{
    public async Task<Result<IReadOnlyList<ConversationDto>>> Handle(ListConversationsQuery request, CancellationToken ct) =>
        Result.Success(await reader.ReadAsync(currentUser.RequiredUserId, null, ct));
}

public sealed record GetConversationQuery(Guid ConversationId) : IQuery<Result<ConversationDto>>, IConversationMemberRequest;

public sealed class GetConversationHandler(ConversationReader reader, ICurrentUser currentUser)
    : IRequestHandler<GetConversationQuery, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(GetConversationQuery request, CancellationToken ct)
    {
        var items = await reader.ReadAsync(currentUser.RequiredUserId, request.ConversationId, ct);
        return items.Count == 0 ? Error.NotFound("Conversation") : items[0];
    }
}

/// <summary>CNV-02: 404 means "no chat yet" and the client shows a draft for that peer.</summary>
public sealed record GetDirectByPeerQuery(Guid PeerUserId) : IQuery<Result<ConversationDto>>;

public sealed class GetDirectByPeerHandler(IApplicationDbContext db, ConversationReader reader, ICurrentUser currentUser)
    : IRequestHandler<GetDirectByPeerQuery, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(GetDirectByPeerQuery request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var key = Conversation.DirectKeyFor(me, request.PeerUserId);
        var id = await db.Conversations.Where(c => c.DirectKey == key).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (id is null)
        {
            return Error.NotFound("Conversation");
        }

        var items = await reader.ReadAsync(me, id, ct);
        return items.Count == 0 ? Error.NotFound("Conversation") : items[0];
    }
}

/// <summary>Membership probe for hub methods (typing, open conversation). The behavior does the actual check.</summary>
public sealed record CheckMembershipQuery(Guid ConversationId) : IQuery<Result>, IConversationMemberRequest;

public sealed class CheckMembershipHandler : IRequestHandler<CheckMembershipQuery, Result>
{
    public Task<Result> Handle(CheckMembershipQuery request, CancellationToken ct) => Task.FromResult(Result.Success());
}

public sealed record ListMembersQuery(Guid ConversationId) : IQuery<Result<IReadOnlyList<MemberDto>>>, IConversationMemberRequest;

public sealed class ListMembersHandler(IApplicationDbContext db, IPresenceTracker presence)
    : IRequestHandler<ListMembersQuery, Result<IReadOnlyList<MemberDto>>>
{
    public async Task<Result<IReadOnlyList<MemberDto>>> Handle(ListMembersQuery request, CancellationToken ct)
    {
        var rows = await (
            from p in db.Participants
            join u in db.Users on p.UserId equals u.Id
            where p.ConversationId == request.ConversationId && p.LeftAt == null
            orderby p.Role, p.JoinedAt
            select new { p.UserId, u.Username, u.DisplayName, p.Role, p.JoinedAt, u.Status, u.LastSeenAt })
            .AsNoTracking().ToListAsync(ct);

        IReadOnlyList<MemberDto> members = rows.Select(r =>
        {
            var deleted = r.Status == UserStatus.Deleted;
            return new MemberDto(r.UserId, r.Username, r.DisplayName, r.Role.ToString(), r.JoinedAt, deleted,
                deleted ? null : presence.IsOnline(r.UserId), deleted ? null : r.LastSeenAt);
        }).ToList();
        return Result.Success(members);
    }
}
