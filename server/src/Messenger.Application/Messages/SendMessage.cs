using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Messenger.Domain.Messages;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Messages;

public sealed record AttachmentInput(string FileName, long SizeBytes, string ContentType);

/// <summary><see cref="Created"/> is false when an idempotent retry returned the already stored message.</summary>
public sealed record SendMessageResult(MessageDto Message, bool Created);

// MSG-01 / MSG-02 / MSG-06 / API-13
public sealed record SendMessageCommand(Guid ConversationId, Guid ClientMessageId, string Body, Guid? ReplyToMessageId,
    IReadOnlyList<AttachmentInput>? Attachments)
    : ICommand<Result<SendMessageResult>>, IConversationMemberRequest, IRetryOnUniqueViolation;

// CNV-01 / CNV-02: the personal chat is created together with its first message.
public sealed record SendDirectMessageCommand(Guid PeerUserId, Guid ClientMessageId, string Body, Guid? ReplyToMessageId,
    IReadOnlyList<AttachmentInput>? Attachments)
    : ICommand<Result<SendMessageResult>>, IRetryOnUniqueViolation;

public sealed class SendMessageValidator : AbstractValidator<SendMessageCommand>
{
    public SendMessageValidator()
    {
        RuleFor(x => x.ClientMessageId).NotEmpty().WithMessage("clientMessageId is required.");
        RuleFor(x => x.Body).ValidMessageBody();
        RuleFor(x => x.Attachments).SetValidator(new AttachmentsValidator()!);
    }
}

public sealed class SendDirectMessageValidator : AbstractValidator<SendDirectMessageCommand>
{
    public SendDirectMessageValidator()
    {
        RuleFor(x => x.PeerUserId).NotEmpty();
        RuleFor(x => x.ClientMessageId).NotEmpty().WithMessage("clientMessageId is required.");
        RuleFor(x => x.Body).ValidMessageBody();
        RuleFor(x => x.Attachments).SetValidator(new AttachmentsValidator()!);
    }
}

public sealed class AttachmentsValidator : AbstractValidator<IReadOnlyList<AttachmentInput>?>
{
    public AttachmentsValidator()
    {
        RuleFor(x => x!.Count).LessThanOrEqualTo(Message.MaxAttachments).When(x => x is not null)
            .WithMessage($"At most {Message.MaxAttachments} attachments are allowed.");
        RuleForEach(x => x).ChildRules(a =>
        {
            a.RuleFor(x => x.FileName).NotEmpty().MaximumLength(255)
                .Must(n => !InputRules.HasForbiddenControlChars(n, false) && !n.Contains('/') && !n.Contains('\\'))
                .WithMessage("File name is not valid.");
            a.RuleFor(x => x.SizeBytes).InclusiveBetween(0, MessageAttachment.MaxSizeBytes);
            a.RuleFor(x => x.ContentType).NotEmpty().MaximumLength(127).Matches(@"^[\w.+-]+/[\w.+-]+$")
                .WithMessage("Content type is not valid.");
        }).When(x => x is not null);
    }
}

public sealed class SendMessageHandler(MessageSender sender, ICurrentUser currentUser)
    : IRequestHandler<SendMessageCommand, Result<SendMessageResult>>
{
    public Task<Result<SendMessageResult>> Handle(SendMessageCommand r, CancellationToken ct) =>
        sender.SendAsync(currentUser.RequiredUserId, r.ConversationId, r.ClientMessageId, r.Body, r.ReplyToMessageId, r.Attachments, ct);
}

public sealed class SendDirectMessageHandler(MessageSender sender, IConversationRepository conversations, IApplicationDbContext db,
    IMessageRepository messages, ICurrentUser currentUser, IClock clock) : IRequestHandler<SendDirectMessageCommand, Result<SendMessageResult>>
{
    public async Task<Result<SendMessageResult>> Handle(SendDirectMessageCommand r, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        if (r.PeerUserId == me)
        {
            return Error.Validation("peerUserId", "You cannot start a chat with yourself.");
        }

        var existing = await messages.FindByClientIdAsync(me, r.ClientMessageId, ct);
        if (existing is not null)
        {
            return await sender.SendAsync(me, existing.ConversationId, r.ClientMessageId, r.Body, r.ReplyToMessageId, r.Attachments, ct);
        }

        var peerAvailable = await db.Users.AnyAsync(u => u.Id == r.PeerUserId && u.Status == UserStatus.Active && u.EmailConfirmed, ct);
        if (!peerAvailable)
        {
            return Error.NotFound("User");
        }

        var conversationId = await conversations.GetOrCreateDirectAsync(me, r.PeerUserId, clock.UtcNow, ct);
        return await sender.SendAsync(me, conversationId, r.ClientMessageId, r.Body, r.ReplyToMessageId, r.Attachments, ct);
    }
}

/// <summary>The heart of the system (doc 02 §5.1): idempotency check → sequence allocation → insert, all in one transaction.
/// Fan-out happens after commit via the <see cref="MessageSent"/> event.</summary>
public sealed class MessageSender(IApplicationDbContext db, IConversationRepository conversations, IMessageRepository messages,
    IUnitOfWork unitOfWork, MessageReader reader, IClock clock)
{
    public async Task<Result<SendMessageResult>> SendAsync(Guid senderId, Guid conversationId, Guid clientMessageId, string body,
        Guid? replyToMessageId, IReadOnlyList<AttachmentInput>? attachments, CancellationToken ct)
    {
        var existing = await messages.FindByClientIdAsync(senderId, clientMessageId, ct);
        if (existing is not null)
        {
            if (existing.ConversationId != conversationId)
            {
                return Error.Conflict("client_message_id_reused", "This clientMessageId was already used in another conversation.");
            }

            return new SendMessageResult(await ReadOneAsync(existing.Id, senderId, conversationId, ct), false);
        }

        var context = await (
            from p in db.Participants
            join c in db.Conversations on p.ConversationId equals c.Id
            where p.ConversationId == conversationId && p.UserId == senderId && p.LeftAt == null
            select new { c.Type, c.DeletedAt, p.VisibleFromSequence, p.ClearedUpToSequence }).AsNoTracking().FirstOrDefaultAsync(ct);
        if (context is null)
        {
            return Error.NotFound("Conversation");
        }

        if (context.DeletedAt is not null)
        {
            return Error.Conflict("conversation_deleted", "This conversation has been deleted.");
        }

        if (context.Type == ConversationType.Direct)
        {
            var peerDeleted = await (
                from p in db.Participants
                join u in db.Users on p.UserId equals u.Id
                where p.ConversationId == conversationId && p.UserId != senderId && u.Status == UserStatus.Deleted
                select u.Id).AnyAsync(ct);
            if (peerDeleted)
            {
                return Error.Conflict("peer_deleted", "This account has been deleted.");
            }
        }

        if (replyToMessageId is { } replyId)
        {
            var floor = Math.Max(context.VisibleFromSequence - 1, context.ClearedUpToSequence);
            var replyVisible = await db.Messages.AnyAsync(m => m.Id == replyId && m.ConversationId == conversationId && m.Sequence > floor, ct);
            if (!replyVisible)
            {
                return Error.Validation("replyToMessageId", "The message you are replying to is not available.");
            }
        }

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == senderId, ct);
        var now = clock.UtcNow;
        var sequence = await conversations.AllocateSequenceAsync(conversationId, now, touchLastMessage: true, ct);
        if (sequence is null)
        {
            return Error.Conflict("conversation_deleted", "This conversation has been deleted.");
        }

        var message = Message.Create(conversationId, sequence.Value, senderId, user.Username, user.DisplayName,
            InputRules.Normalize(body), clientMessageId, replyToMessageId,
            (attachments ?? []).Select(a => (InputRules.Normalize(a.FileName), a.SizeBytes, a.ContentType.ToLowerInvariant())), now);
        messages.Add(message);
        await unitOfWork.SaveChangesAsync(ct);

        return new SendMessageResult(await ReadOneAsync(message.Id, senderId, conversationId, ct), true);
    }

    private async Task<MessageDto> ReadOneAsync(Guid messageId, Guid viewerId, Guid conversationId, CancellationToken ct)
    {
        var floor = await db.Participants.Where(p => p.ConversationId == conversationId && p.UserId == viewerId)
            .Select(p => p.VisibleFromSequence - 1 > p.ClearedUpToSequence ? p.VisibleFromSequence - 1 : p.ClearedUpToSequence)
            .FirstOrDefaultAsync(ct);
        return (await reader.ReadAsync(db.Messages.Where(m => m.Id == messageId), viewerId, floor, ct))[0];
    }
}
