using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Messages;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Messages;

// MSG-04 / MSG-08: only the author edits, within 48 h; edits take a new number from the conversation counter (P-11).
public sealed record EditMessageCommand(Guid MessageId, string Body) : ICommand<Result<MessageDto>>, IMessageAccessRequest;

public sealed class EditMessageValidator : AbstractValidator<EditMessageCommand>
{
    public EditMessageValidator() => RuleFor(x => x.Body).ValidMessageBody();
}

public sealed class EditMessageHandler(IMessageRepository messages, IConversationRepository conversations, IApplicationDbContext db,
    IUnitOfWork unitOfWork, MessageReader reader, ICurrentUser currentUser, IClock clock) : IRequestHandler<EditMessageCommand, Result<MessageDto>>
{
    public async Task<Result<MessageDto>> Handle(EditMessageCommand request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var now = clock.UtcNow;
        var message = await messages.GetAsync(request.MessageId, ct);
        if (message!.SenderId != me)
        {
            return Error.Forbidden("Only the author can edit a message.");
        }

        if (!message.CanEdit(me, now))
        {
            return message.Edit(me, request.Body, 0, now).Error!;
        }

        var sequence = await conversations.AllocateSequenceAsync(message.ConversationId, now, touchLastMessage: false, ct);
        if (sequence is null)
        {
            return Error.Conflict("conversation_deleted", "This conversation has been deleted.");
        }

        var edited = message.Edit(me, InputRules.Normalize(request.Body), sequence.Value, now);
        if (!edited.IsSuccess)
        {
            return edited.Error!;
        }

        await unitOfWork.SaveChangesAsync(ct);
        var floor = await ViewerFloor.GetAsync(db, message.ConversationId, me, ct);
        return (await reader.ReadAsync(db.Messages.Where(m => m.Id == message.Id), me, floor, ct))[0];
    }
}

// MSG-05 / P-14: tombstone delete by the author.
public sealed record DeleteMessageCommand(Guid MessageId) : ICommand<Result>, IMessageAccessRequest;

public sealed class DeleteMessageHandler(IMessageRepository messages, IConversationRepository conversations, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<DeleteMessageCommand, Result>
{
    public async Task<Result> Handle(DeleteMessageCommand request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var message = await messages.GetAsync(request.MessageId, ct);
        if (message!.SenderId != me)
        {
            return Error.Forbidden("Only the author can delete a message.");
        }

        if (message.IsDeleted)
        {
            return Result.Success();
        }

        var now = clock.UtcNow;
        var sequence = await conversations.AllocateSequenceAsync(message.ConversationId, now, touchLastMessage: false, ct);
        if (sequence is null)
        {
            return Error.Conflict("conversation_deleted", "This conversation has been deleted.");
        }

        return message.Delete(me, sequence.Value, now);
    }
}

// MSG-03 / STS-08: cursor pagination. `after` returns messages created OR changed after that sequence (resync).
public sealed record GetMessagesQuery(Guid ConversationId, long? Before, long? After, int? Limit)
    : IQuery<Result<MessagePageDto>>, IConversationMemberRequest;

public sealed class GetMessagesValidator : AbstractValidator<GetMessagesQuery>
{
    public GetMessagesValidator()
    {
        RuleFor(x => x).Must(x => x.Before is null || x.After is null).WithMessage("Use either 'before' or 'after', not both.")
            .OverridePropertyName("before");
        RuleFor(x => x.Limit).InclusiveBetween(1, Paging.MaxLimit).When(x => x.Limit is not null);
        RuleFor(x => x.Before).GreaterThan(0).When(x => x.Before is not null);
        RuleFor(x => x.After).GreaterThanOrEqualTo(0).When(x => x.After is not null);
    }
}

public sealed class GetMessagesHandler(IApplicationDbContext db, MessageReader reader, ICurrentUser currentUser)
    : IRequestHandler<GetMessagesQuery, Result<MessagePageDto>>
{
    public async Task<Result<MessagePageDto>> Handle(GetMessagesQuery request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var limit = request.Limit ?? Paging.MaxLimit;
        var floor = await ViewerFloor.GetAsync(db, request.ConversationId, me, ct);
        var visible = db.Messages.AsNoTracking().Where(m => m.ConversationId == request.ConversationId && m.Sequence > floor);

        IQueryable<Message> page;
        if (request.After is { } after)
        {
            page = visible.Where(m => m.UpdatedSequence > after).OrderBy(m => m.UpdatedSequence).Take(limit + 1);
        }
        else
        {
            if (request.Before is { } before)
            {
                visible = visible.Where(m => m.Sequence < before);
            }

            page = visible.OrderByDescending(m => m.Sequence).Take(limit + 1);
        }

        var ids = await page.Select(m => m.Id).ToListAsync(ct);
        var hasMore = ids.Count > limit;
        if (hasMore)
        {
            ids = ids.Take(limit).ToList();
        }

        var items = await reader.ReadAsync(db.Messages.Where(m => ids.Contains(m.Id)), me, floor, ct);
        return new MessagePageDto(items, hasMore);
    }
}

// MSG-04: edit history is visible to the author only.
public sealed record GetMessageHistoryQuery(Guid MessageId) : IQuery<Result<IReadOnlyList<EditHistoryEntryDto>>>, IMessageAccessRequest;

public sealed class GetMessageHistoryHandler(IApplicationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMessageHistoryQuery, Result<IReadOnlyList<EditHistoryEntryDto>>>
{
    public async Task<Result<IReadOnlyList<EditHistoryEntryDto>>> Handle(GetMessageHistoryQuery request, CancellationToken ct)
    {
        var isAuthor = await db.Messages.AnyAsync(m => m.Id == request.MessageId && m.SenderId == currentUser.RequiredUserId, ct);
        if (!isAuthor)
        {
            return Error.Forbidden("Only the author can see the edit history.");
        }

        IReadOnlyList<EditHistoryEntryDto> items = await db.MessageEditHistory.AsNoTracking()
            .Where(h => h.MessageId == request.MessageId)
            .OrderBy(h => h.EditedAt)
            .Select(h => new EditHistoryEntryDto(h.PreviousBody, h.EditedAt))
            .ToListAsync(ct);
        return Result.Success(items);
    }
}

// STS-06: per-recipient delivery/read for the author; deliberately without presence.
public sealed record GetMessageInfoQuery(Guid MessageId) : IQuery<Result<MessageInfoDto>>, IMessageAccessRequest;

public sealed class GetMessageInfoHandler(IApplicationDbContext db, MessageReader reader, ICurrentUser currentUser)
    : IRequestHandler<GetMessageInfoQuery, Result<MessageInfoDto>>
{
    public async Task<Result<MessageInfoDto>> Handle(GetMessageInfoQuery request, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking()
            .Where(m => m.Id == request.MessageId)
            .Select(m => new { m.Id, m.SenderId, m.ConversationId, m.Sequence })
            .FirstAsync(ct);
        if (message.SenderId != currentUser.RequiredUserId)
        {
            return Error.Forbidden("Only the author can see message info.");
        }

        var recipients = await (
            from p in db.Participants
            join u in db.Users on p.UserId equals u.Id
            where p.ConversationId == message.ConversationId && p.LeftAt == null && p.UserId != message.SenderId
                  && p.VisibleFromSequence <= message.Sequence
            let receipt = db.MessageReceipts.FirstOrDefault(r => r.MessageId == message.Id && r.UserId == p.UserId)
            orderby u.DisplayName
            select new ReceiptInfoDto(p.UserId, u.Username, u.DisplayName,
                receipt == null ? null : receipt.DeliveredAt, receipt == null ? null : receipt.ReadAt))
            .AsNoTracking().ToListAsync(ct);

        var status = (await reader.GetStatusesAsync([message.Id], ct)).GetValueOrDefault(message.Id);
        return new MessageInfoDto(message.Id, status.ToString(), recipients);
    }
}

// STS-08: statuses of my own messages for resync after reconnect.
public sealed record GetStatusesQuery(Guid ConversationId, long FromSequence) : IQuery<Result<IReadOnlyList<MessageStatusDto>>>, IConversationMemberRequest;

public sealed class GetStatusesHandler(IApplicationDbContext db, MessageReader reader, ICurrentUser currentUser)
    : IRequestHandler<GetStatusesQuery, Result<IReadOnlyList<MessageStatusDto>>>
{
    public async Task<Result<IReadOnlyList<MessageStatusDto>>> Handle(GetStatusesQuery request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var floor = await ViewerFloor.GetAsync(db, request.ConversationId, me, ct);
        var own = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.SenderId == me && m.Sequence > floor
                        && m.Sequence >= request.FromSequence && m.DeletedAt == null)
            .OrderByDescending(m => m.Sequence).Take(500)
            .Select(m => new { m.Id, m.Sequence })
            .ToListAsync(ct);
        var statuses = await reader.GetStatusesAsync(own.Select(m => m.Id).ToList(), ct);
        IReadOnlyList<MessageStatusDto> items = own.OrderBy(m => m.Sequence)
            .Select(m => new MessageStatusDto(m.Id, m.Sequence, statuses.GetValueOrDefault(m.Id).ToString()))
            .ToList();
        return Result.Success(items);
    }
}

public sealed record MessagesDelivered(Guid UserId, IReadOnlyList<Guid> MessageIds) : IDomainEvent;

public sealed record MessagesRead(Guid ConversationId, Guid ReaderId, long FromSequenceExclusive, long UpToSequence) : IDomainEvent;

// STS-02: idempotent delivery ack (hub AckDelivered).
public sealed record AckDeliveredCommand(IReadOnlyList<Guid> MessageIds) : ICommand<Result<int>>;

public sealed class AckDeliveredValidator : AbstractValidator<AckDeliveredCommand>
{
    public AckDeliveredValidator() => RuleFor(x => x.MessageIds).NotNull().Must(ids => ids.Count <= 200)
        .WithMessage("Acknowledge at most 200 messages at once.");
}

public sealed class AckDeliveredHandler(IMessageRepository messages, IDomainEventCollector events, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<AckDeliveredCommand, Result<int>>
{
    public async Task<Result<int>> Handle(AckDeliveredCommand request, CancellationToken ct)
    {
        if (request.MessageIds.Count == 0)
        {
            return 0;
        }

        var me = currentUser.RequiredUserId;
        var delivered = await messages.MarkDeliveredAsync(me, request.MessageIds.Distinct().ToList(), clock.UtcNow, ct);
        if (delivered.Count > 0)
        {
            events.Add(new MessagesDelivered(me, delivered));
        }

        return delivered.Count;
    }
}

// STS-03: set-based read marking up to a sequence (hub MarkRead, REST fallback).
public sealed record MarkReadCommand(Guid ConversationId, long UpToSequence) : ICommand<Result<long>>, IConversationMemberRequest;

public sealed class MarkReadValidator : AbstractValidator<MarkReadCommand>
{
    public MarkReadValidator() => RuleFor(x => x.UpToSequence).GreaterThanOrEqualTo(0);
}

public sealed class MarkReadHandler(IConversationRepository conversations, IMessageRepository messages, IDomainEventCollector events,
    ICurrentUser currentUser, IClock clock) : IRequestHandler<MarkReadCommand, Result<long>>
{
    public async Task<Result<long>> Handle(MarkReadCommand request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        var participant = conversation!.ActiveParticipant(me)!;
        var upTo = Math.Min(request.UpToSequence, conversation.LastSequence);
        var previous = participant.LastReadSequence;
        if (upTo <= previous)
        {
            return previous;
        }

        participant.MarkReadUpTo(upTo);
        var from = Math.Max(previous, participant.VisibilityFloor);
        await messages.MarkReadAsync(me, conversation.Id, from, upTo, clock.UtcNow, ct);
        events.Add(new MessagesRead(conversation.Id, me, from, upTo));
        return upTo;
    }
}

internal static class ViewerFloor
{
    public static Task<long> GetAsync(IApplicationDbContext db, Guid conversationId, Guid userId, CancellationToken ct) =>
        db.Participants.Where(p => p.ConversationId == conversationId && p.UserId == userId)
            .Select(p => p.VisibleFromSequence - 1 > p.ClearedUpToSequence ? p.VisibleFromSequence - 1 : p.ClearedUpToSequence)
            .FirstOrDefaultAsync(ct);
}
