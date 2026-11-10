using Messenger.Domain.Common;

namespace Messenger.Domain.Messages;

public enum MessageStatus { Sent, Delivered, Read }

public sealed class Message : Entity
{
    public const string DeletedPlaceholder = "[Message deleted]";
    public static readonly TimeSpan EditWindow = TimeSpan.FromHours(48);
    public const int MaxAttachments = 10;

    private readonly List<MessageEditHistory> _editHistory = [];
    private readonly List<MessageAttachment> _attachments = [];

    private Message() { }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public long Sequence { get; private set; }
    public long UpdatedSequence { get; private set; }
    public Guid SenderId { get; private set; }
    public string SenderUsernameSnapshot { get; private set; } = null!;
    public string SenderDisplayNameSnapshot { get; private set; } = null!;
    public string? Body { get; private set; }
    public Guid? ReplyToMessageId { get; private set; }
    public Guid ClientMessageId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EditedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public IReadOnlyCollection<MessageEditHistory> EditHistory => _editHistory;
    public IReadOnlyCollection<MessageAttachment> Attachments => _attachments;

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>MSG-01/MSG-10: the sequence comes from the per-conversation counter; sender names are snapshotted.</summary>
    public static Message Create(Guid conversationId, long sequence, Guid senderId, string senderUsername,
        string senderDisplayName, string body, Guid clientMessageId, Guid? replyToMessageId,
        IEnumerable<(string FileName, long SizeBytes, string ContentType)> attachments, DateTimeOffset now)
    {
        var message = new Message
        {
            Id = Guid.CreateVersion7(now),
            ConversationId = conversationId,
            Sequence = sequence,
            UpdatedSequence = sequence,
            SenderId = senderId,
            SenderUsernameSnapshot = senderUsername,
            SenderDisplayNameSnapshot = senderDisplayName,
            Body = body,
            ClientMessageId = clientMessageId,
            ReplyToMessageId = replyToMessageId,
            CreatedAt = now,
        };
        foreach (var (fileName, size, contentType) in attachments)
        {
            message._attachments.Add(MessageAttachment.Create(message.Id, fileName, size, contentType, now));
        }

        message.Raise(new MessageSent(message.Id, conversationId, senderId));
        return message;
    }

    public bool CanEdit(Guid userId, DateTimeOffset now) =>
        userId == SenderId && !IsDeleted && now - CreatedAt <= EditWindow;

    /// <summary>MSG-04: only the author, within 48 hours; the previous body goes to the author-only history.</summary>
    public Result Edit(Guid editorId, string newBody, long newSequence, DateTimeOffset now)
    {
        if (editorId != SenderId)
        {
            return Error.Forbidden("Only the author can edit a message.");
        }

        if (IsDeleted)
        {
            return Error.Conflict("message_deleted", "A deleted message cannot be edited.");
        }

        if (now - CreatedAt > EditWindow)
        {
            return Error.Conflict("edit_window_expired", "Messages can only be edited within 48 hours.");
        }

        if (string.Equals(Body, newBody, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        _editHistory.Add(MessageEditHistory.Create(Id, Body!, now));
        Body = newBody;
        EditedAt = now;
        UpdatedSequence = newSequence;
        Raise(new MessageEdited(Id, ConversationId));
        return Result.Success();
    }

    /// <summary>MSG-05 / P-14: tombstone. Body and edit history are erased; the row and sequence stay.</summary>
    public Result Delete(Guid actorId, long newSequence, DateTimeOffset now)
    {
        if (actorId != SenderId)
        {
            return Error.Forbidden("Only the author can delete a message.");
        }

        if (IsDeleted)
        {
            return Result.Success();
        }

        Body = null;
        DeletedAt = now;
        UpdatedSequence = newSequence;
        _editHistory.Clear();
        _attachments.Clear();
        Raise(new MessageDeleted(Id, ConversationId, Sequence));
        return Result.Success();
    }
}

public sealed class MessageEditHistory
{
    private MessageEditHistory() { }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string PreviousBody { get; private set; } = null!;
    public DateTimeOffset EditedAt { get; private set; }

    public static MessageEditHistory Create(Guid messageId, string previousBody, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        MessageId = messageId,
        PreviousBody = previousBody,
        EditedAt = now,
    };
}

/// <summary>STS-04: receipts are created lazily on ack; "not delivered" means no row.</summary>
public sealed class MessageReceipt
{
    private MessageReceipt() { }

    public Guid MessageId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset DeliveredAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
}

/// <summary>MSG-09: attachment metadata only; real storage is out of scope.</summary>
public sealed class MessageAttachment
{
    public const long MaxSizeBytes = 100L * 1024 * 1024;

    private MessageAttachment() { }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string FileName { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string ContentType { get; private set; } = null!;
    public string? StorageKey { get; private set; }

    public static MessageAttachment Create(Guid messageId, string fileName, long sizeBytes, string contentType, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        MessageId = messageId,
        FileName = fileName,
        SizeBytes = sizeBytes,
        ContentType = contentType,
    };
}

/// <summary>STS-05 / P-07: Read only if every recipient read; Delivered only if every recipient received.</summary>
public static class MessageStatusRules
{
    public static MessageStatus Aggregate(int recipientCount, int deliveredCount, int readCount)
    {
        if (recipientCount <= 0)
        {
            return MessageStatus.Sent;
        }

        if (readCount >= recipientCount)
        {
            return MessageStatus.Read;
        }

        return deliveredCount >= recipientCount ? MessageStatus.Delivered : MessageStatus.Sent;
    }
}

public sealed record MessageSent(Guid MessageId, Guid ConversationId, Guid SenderId) : IDomainEvent;

public sealed record MessageEdited(Guid MessageId, Guid ConversationId) : IDomainEvent;

public sealed record MessageDeleted(Guid MessageId, Guid ConversationId, long Sequence) : IDomainEvent;
