using Messenger.Domain.Common;

namespace Messenger.Domain.Conversations;

public enum ConversationType { Direct, Group }

public enum ParticipantRole { Owner, Admin, Member }

public sealed class Conversation : Entity
{
    public const int MinGroupMembersIncludingCreator = 3;
    public const int MaxGroupMembers = 200;

    private readonly List<ConversationParticipant> _participants = [];

    private Conversation() { }

    public Guid Id { get; private set; }
    public ConversationType Type { get; private set; }
    public string? DirectKey { get; private set; }
    public string? Title { get; private set; }
    public Guid CreatedById { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public long LastSequence { get; private set; }
    public DateTimeOffset? LastMessageAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public IReadOnlyCollection<ConversationParticipant> Participants => _participants;

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>P-13: one direct conversation per unordered pair of users.</summary>
    public static string DirectKeyFor(Guid a, Guid b) => a.CompareTo(b) <= 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";

    /// <summary>CNV-04: a group is created together with its members; the creator becomes Owner.</summary>
    public static Result<Conversation> CreateGroup(Guid creatorId, string title, IReadOnlyCollection<Guid> memberIds, DateTimeOffset now)
    {
        var normalizedTitle = InputRules.Normalize(title);
        if (InputRules.ValidateGroupTitle(normalizedTitle) is { } titleError)
        {
            return Error.Validation("title", titleError);
        }

        var others = memberIds.Where(id => id != creatorId).Distinct().ToList();
        if (others.Count + 1 < MinGroupMembersIncludingCreator)
        {
            return Error.Validation("memberIds", $"A group needs at least {MinGroupMembersIncludingCreator} members including you.");
        }

        if (others.Count + 1 > MaxGroupMembers)
        {
            return Error.Validation("memberIds", $"A group can have at most {MaxGroupMembers} members.");
        }

        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(now),
            Type = ConversationType.Group,
            Title = normalizedTitle,
            CreatedById = creatorId,
            CreatedAt = now,
        };
        conversation._participants.Add(ConversationParticipant.Join(conversation.Id, creatorId, ParticipantRole.Owner, 0, now));
        foreach (var memberId in others)
        {
            conversation._participants.Add(ConversationParticipant.Join(conversation.Id, memberId, ParticipantRole.Member, 0, now));
        }

        conversation.Raise(new ConversationChanged(conversation.Id, []));
        return conversation;
    }

    public ConversationParticipant? ActiveParticipant(Guid userId) =>
        _participants.FirstOrDefault(p => p.UserId == userId && p.IsActive);

    public IEnumerable<ConversationParticipant> ActiveParticipants => _participants.Where(p => p.IsActive);

    public Result Rename(Guid actorId, string title)
    {
        if (RequireActiveGroup(actorId, GroupPermission.Rename) is { } error)
        {
            return error;
        }

        var normalized = InputRules.Normalize(title);
        if (InputRules.ValidateGroupTitle(normalized) is { } titleError)
        {
            return Error.Validation("title", titleError);
        }

        Title = normalized;
        Raise(new ConversationChanged(Id, []));
        return Result.Success();
    }

    /// <summary>CNV-05: new members only see messages sent after they joined.</summary>
    public Result AddMembers(Guid actorId, IReadOnlyCollection<Guid> userIds, DateTimeOffset now)
    {
        if (RequireActiveGroup(actorId, GroupPermission.ManageMembers) is { } error)
        {
            return error;
        }

        var toAdd = userIds.Distinct().Where(id => ActiveParticipant(id) is null).ToList();
        if (ActiveParticipants.Count() + toAdd.Count > MaxGroupMembers)
        {
            return Error.Validation("userIds", $"A group can have at most {MaxGroupMembers} members.");
        }

        foreach (var userId in toAdd)
        {
            var existing = _participants.FirstOrDefault(p => p.UserId == userId);
            if (existing is not null)
            {
                existing.Rejoin(LastSequence, now);
            }
            else
            {
                _participants.Add(ConversationParticipant.Join(Id, userId, ParticipantRole.Member, LastSequence, now));
            }
        }

        if (toAdd.Count > 0)
        {
            Raise(new ConversationChanged(Id, []));
        }

        return Result.Success();
    }

    public Result RemoveMember(Guid actorId, Guid targetId, DateTimeOffset now)
    {
        if (actorId == targetId)
        {
            return Leave(actorId, now);
        }

        if (RequireActiveGroup(actorId, GroupPermission.ManageMembers) is { } error)
        {
            return error;
        }

        var actor = ActiveParticipant(actorId)!;
        var target = ActiveParticipant(targetId);
        if (target is null)
        {
            return Error.NotFound("Member");
        }

        if (!GroupPermissions.CanRemove(actor.Role, target.Role))
        {
            return Error.Forbidden("You cannot remove this member.");
        }

        target.MarkLeft(now);
        Raise(new ConversationChanged(Id, [targetId]));
        return Result.Success();
    }

    public Result ChangeRole(Guid actorId, Guid targetId, ParticipantRole role)
    {
        if (RequireActiveGroup(actorId, GroupPermission.ChangeRoles) is { } error)
        {
            return error;
        }

        if (role == ParticipantRole.Owner)
        {
            return Error.Validation("role", "Ownership cannot be assigned directly.");
        }

        var target = ActiveParticipant(targetId);
        if (target is null)
        {
            return Error.NotFound("Member");
        }

        if (target.Role == ParticipantRole.Owner)
        {
            return Error.Forbidden("The owner's role cannot be changed.");
        }

        target.ChangeRole(role);
        Raise(new ConversationChanged(Id, []));
        return Result.Success();
    }

    /// <summary>CNV-06: leaving keeps history for others; ownership moves to the oldest admin, then oldest member.</summary>
    public Result Leave(Guid userId, DateTimeOffset now)
    {
        if (Type != ConversationType.Group)
        {
            return Error.BadRequest("not_a_group", "Only groups can be left. Clear a personal chat instead.");
        }

        var participant = ActiveParticipant(userId);
        if (participant is null)
        {
            return Error.NotFound("Conversation");
        }

        participant.MarkLeft(now);
        if (participant.Role == ParticipantRole.Owner)
        {
            participant.ChangeRole(ParticipantRole.Member);
            var successor = ActiveParticipants
                .OrderBy(p => p.Role == ParticipantRole.Admin ? 0 : 1)
                .ThenBy(p => p.JoinedAt)
                .FirstOrDefault();
            successor?.ChangeRole(ParticipantRole.Owner);
        }

        if (!ActiveParticipants.Any())
        {
            DeletedAt ??= now;
        }

        Raise(new ConversationChanged(Id, [userId]));
        return Result.Success();
    }

    /// <summary>CNV-07: soft delete; history stays readable, new actions are rejected.</summary>
    public Result Delete(Guid actorId, DateTimeOffset now)
    {
        if (RequireActiveGroup(actorId, GroupPermission.DeleteGroup) is { } error)
        {
            return error;
        }

        DeletedAt = now;
        Raise(new ConversationChanged(Id, []));
        return Result.Success();
    }

    /// <summary>CNV-03: "deleting" a personal chat only moves this user's watermark.</summary>
    public Result Clear(Guid userId)
    {
        if (Type != ConversationType.Direct)
        {
            return Error.BadRequest("not_direct", "Only personal chats can be cleared. Leave a group instead.");
        }

        var participant = ActiveParticipant(userId);
        if (participant is null)
        {
            return Error.NotFound("Conversation");
        }

        participant.ClearUpTo(LastSequence);
        return Result.Success();
    }

    private Error? RequireActiveGroup(Guid actorId, GroupPermission permission)
    {
        var actor = ActiveParticipant(actorId);
        if (actor is null)
        {
            return Error.NotFound("Conversation");
        }

        if (Type != ConversationType.Group)
        {
            return Error.BadRequest("not_a_group", "This action is only available in groups.");
        }

        if (IsDeleted)
        {
            return Error.Conflict("conversation_deleted", "This group has been deleted.");
        }

        return GroupPermissions.Has(actor.Role, permission) ? null : Error.Forbidden();
    }
}

public sealed record ConversationChanged(Guid ConversationId, IReadOnlyCollection<Guid> RemovedUserIds) : IDomainEvent;
