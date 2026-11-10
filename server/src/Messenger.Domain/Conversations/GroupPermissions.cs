namespace Messenger.Domain.Conversations;

public enum GroupPermission { Rename, ManageMembers, ChangeRoles, DeleteGroup }

/// <summary>RBAC-02: the group role matrix (doc 02 §6.4). Admins never edit or delete other people's messages.</summary>
public static class GroupPermissions
{
    public static bool Has(ParticipantRole role, GroupPermission permission) => permission switch
    {
        GroupPermission.Rename => role is ParticipantRole.Owner or ParticipantRole.Admin,
        GroupPermission.ManageMembers => role is ParticipantRole.Owner or ParticipantRole.Admin,
        GroupPermission.ChangeRoles => role is ParticipantRole.Owner,
        GroupPermission.DeleteGroup => role is ParticipantRole.Owner,
        _ => false,
    };

    /// <summary>Owners may remove anyone; admins may remove plain members only.</summary>
    public static bool CanRemove(ParticipantRole actor, ParticipantRole target) => actor switch
    {
        ParticipantRole.Owner => target != ParticipantRole.Owner,
        ParticipantRole.Admin => target == ParticipantRole.Member,
        _ => false,
    };
}
