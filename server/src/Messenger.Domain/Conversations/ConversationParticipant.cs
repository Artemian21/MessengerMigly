namespace Messenger.Domain.Conversations;

/// <summary>
/// Membership plus per-user sequence watermarks (P-06). A message with sequence S is visible to this participant
/// when S &gt; <see cref="VisibilityFloor"/>.
/// </summary>
public sealed class ConversationParticipant
{
    private ConversationParticipant() { }

    public Guid ConversationId { get; private set; }
    public Guid UserId { get; private set; }
    public ParticipantRole Role { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
    public long VisibleFromSequence { get; private set; }
    public long ClearedUpToSequence { get; private set; }
    public long LastReadSequence { get; private set; }
    public DateTimeOffset? ClearedAt { get; private set; }
    public DateTimeOffset? LeftAt { get; private set; }

    public bool IsActive => LeftAt is null;

    public long VisibilityFloor => Math.Max(VisibleFromSequence - 1, ClearedUpToSequence);

    public bool CanSee(long sequence) => sequence > VisibilityFloor;

    public static ConversationParticipant Join(Guid conversationId, Guid userId, ParticipantRole role, long lastSequence,
        DateTimeOffset now) => new()
    {
        ConversationId = conversationId,
        UserId = userId,
        Role = role,
        JoinedAt = now,
        VisibleFromSequence = lastSequence + 1,
        LastReadSequence = lastSequence,
    };

    public void Rejoin(long lastSequence, DateTimeOffset now)
    {
        LeftAt = null;
        Role = ParticipantRole.Member;
        JoinedAt = now;
        VisibleFromSequence = lastSequence + 1;
        ClearedUpToSequence = 0;
        LastReadSequence = lastSequence;
    }

    public void MarkLeft(DateTimeOffset now) => LeftAt = now;

    public void ChangeRole(ParticipantRole role) => Role = role;

    public void ClearUpTo(long sequence)
    {
        ClearedUpToSequence = Math.Max(ClearedUpToSequence, sequence);
        LastReadSequence = Math.Max(LastReadSequence, sequence);
    }

    public void MarkReadUpTo(long sequence) => LastReadSequence = Math.Max(LastReadSequence, sequence);
}
