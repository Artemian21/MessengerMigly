namespace Messenger.Application.Common;

public sealed record MeDto(
    Guid Id,
    string Email,
    string Username,
    string DisplayName,
    string Role,
    bool EmailConfirmed,
    bool NeedsProfile,
    bool HasPassword,
    DateTimeOffset CreatedAt);

public sealed record UserSummaryDto(Guid Id, string Username, string DisplayName);

/// <summary>Presence fields are only filled when the viewer shares a conversation with the user (PRS-04).</summary>
public sealed record PublicUserDto(Guid Id, string Username, string DisplayName, bool IsDeleted, bool? Online, DateTimeOffset? LastSeenAt);

public sealed record SenderDto(Guid Id, string Username, string DisplayName, bool IsDeleted);

public sealed record ReplyPreviewDto(Guid Id, long Sequence, string SenderDisplayName, string? BodyPreview, bool IsDeleted);

public sealed record AttachmentDto(Guid Id, string FileName, long SizeBytes, string ContentType);

/// <summary>Message as seen by a participant. <see cref="Status"/> is only set for the caller's own messages.</summary>
public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    long UpdatedSequence,
    SenderDto Sender,
    string? Body,
    bool IsDeleted,
    bool IsEdited,
    ReplyPreviewDto? ReplyTo,
    Guid ClientMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<AttachmentDto> Attachments,
    string? Status);

public sealed record MessagePreviewDto(Guid Id, long Sequence, Guid SenderId, string SenderDisplayName, string? Body, bool IsDeleted, DateTimeOffset CreatedAt);

public sealed record ConversationDto(
    Guid Id,
    string Type,
    string? Title,
    PublicUserDto? Peer,
    string MyRole,
    int MemberCount,
    long LastSequence,
    long LastReadSequence,
    long VisibleFromSequence,
    int UnreadCount,
    MessagePreviewDto? LastMessage,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset CreatedAt,
    bool IsDeleted);

public sealed record MemberDto(Guid UserId, string Username, string DisplayName, string Role, DateTimeOffset JoinedAt, bool IsDeleted, bool? Online, DateTimeOffset? LastSeenAt);

public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, bool HasMore);

public sealed record MessageStatusDto(Guid MessageId, long Sequence, string Status);

public sealed record ReceiptInfoDto(Guid UserId, string Username, string DisplayName, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt);

public sealed record MessageInfoDto(Guid MessageId, string Status, IReadOnlyList<ReceiptInfoDto> Recipients);

public sealed record EditHistoryEntryDto(string PreviousBody, DateTimeOffset EditedAt);

public sealed record SearchHitDto(Guid MessageId, Guid ConversationId, string? ConversationTitle, long Sequence, SenderDto Sender, string Snippet, DateTimeOffset CreatedAt);

public sealed record SessionDto(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset ExpiresAt, string? UserAgent, string? Ip, bool Current);

public sealed record AdminUserDto(Guid Id, string Email, string Username, string DisplayName, string Role, string Status, bool EmailConfirmed, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

public sealed record AuditLogDto(Guid Id, Guid? ActorUserId, string Action, string EntityType, string? EntityId, string? DataJson, string? Ip, DateTimeOffset CreatedAt);
