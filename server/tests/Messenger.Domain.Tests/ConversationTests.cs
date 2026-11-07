using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Shouldly;

namespace Messenger.Domain.Tests;

public sealed class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();
    private static readonly Guid Dave = Guid.NewGuid();

    private static Conversation Group() => Conversation.CreateGroup(Owner, "Study crew", [Bob, Carol], Now).Value;

    [Fact]
    public void Group_requires_at_least_three_members_including_creator()
    {
        var result = Conversation.CreateGroup(Owner, "Too small", [Bob], Now);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void Creator_becomes_owner_and_others_members()
    {
        var group = Group();

        group.ActiveParticipant(Owner)!.Role.ShouldBe(ParticipantRole.Owner);
        group.ActiveParticipant(Bob)!.Role.ShouldBe(ParticipantRole.Member);
    }

    [Fact]
    public void Direct_key_is_order_independent()
    {
        Conversation.DirectKeyFor(Bob, Carol).ShouldBe(Conversation.DirectKeyFor(Carol, Bob));
    }

    [Fact]
    public void Visibility_floor_combines_join_and_clear_watermarks()
    {
        var participant = ConversationParticipant.Join(Guid.NewGuid(), Bob, ParticipantRole.Member, lastSequence: 10, Now);

        participant.VisibleFromSequence.ShouldBe(11);
        participant.CanSee(10).ShouldBeFalse();
        participant.CanSee(11).ShouldBeTrue();

        participant.ClearUpTo(20);
        participant.CanSee(20).ShouldBeFalse();
        participant.CanSee(21).ShouldBeTrue();
        participant.LastReadSequence.ShouldBe(20);
    }

    [Fact]
    public void Clear_never_moves_watermark_backwards()
    {
        var participant = ConversationParticipant.Join(Guid.NewGuid(), Bob, ParticipantRole.Member, 0, Now);
        participant.ClearUpTo(15);
        participant.ClearUpTo(5);

        participant.ClearedUpToSequence.ShouldBe(15);
    }

    [Fact]
    public void Plain_member_cannot_add_members()
    {
        var group = Group();

        var result = group.AddMembers(Bob, [Dave], Now);

        result.Error!.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void Admin_can_remove_member_but_not_owner_or_other_admin()
    {
        var group = Group();
        group.AddMembers(Owner, [Dave], Now).IsSuccess.ShouldBeTrue();
        group.ChangeRole(Owner, Bob, ParticipantRole.Admin).IsSuccess.ShouldBeTrue();
        group.ChangeRole(Owner, Dave, ParticipantRole.Admin).IsSuccess.ShouldBeTrue();

        group.RemoveMember(Bob, Owner, Now).Error!.Type.ShouldBe(ErrorType.Forbidden);
        group.RemoveMember(Bob, Dave, Now).Error!.Type.ShouldBe(ErrorType.Forbidden);
        group.RemoveMember(Bob, Carol, Now).IsSuccess.ShouldBeTrue();
        group.ActiveParticipant(Carol).ShouldBeNull();
    }

    [Fact]
    public void Only_owner_changes_roles_and_ownership_cannot_be_assigned()
    {
        var group = Group();

        group.ChangeRole(Bob, Carol, ParticipantRole.Admin).Error!.Type.ShouldBe(ErrorType.Forbidden);
        group.ChangeRole(Owner, Carol, ParticipantRole.Owner).Error!.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void New_member_sees_only_messages_after_joining()
    {
        var group = Group();
        typeof(Conversation).GetProperty(nameof(Conversation.LastSequence))!.SetValue(group, 42L);

        group.AddMembers(Owner, [Dave], Now).IsSuccess.ShouldBeTrue();

        group.ActiveParticipant(Dave)!.VisibleFromSequence.ShouldBe(43);
    }

    [Fact]
    public void Owner_leaving_transfers_ownership_to_oldest_admin()
    {
        var group = Group();
        group.ChangeRole(Owner, Carol, ParticipantRole.Admin);

        group.Leave(Owner, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        group.ActiveParticipant(Carol)!.Role.ShouldBe(ParticipantRole.Owner);
        group.ActiveParticipant(Owner).ShouldBeNull();
    }

    [Fact]
    public void Owner_leaving_without_admins_transfers_to_oldest_member()
    {
        var group = Group();

        group.Leave(Owner, Now.AddMinutes(1));

        group.ActiveParticipants.Count(p => p.Role == ParticipantRole.Owner).ShouldBe(1);
    }

    [Fact]
    public void Last_member_leaving_soft_deletes_group()
    {
        var group = Group();
        group.Leave(Bob, Now);
        group.Leave(Carol, Now);
        group.Leave(Owner, Now);

        group.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public void Deleted_group_rejects_changes()
    {
        var group = Group();
        group.Delete(Owner, Now).IsSuccess.ShouldBeTrue();

        group.Rename(Owner, "New").Error!.Code.ShouldBe("conversation_deleted");
    }

    [Fact]
    public void Only_owner_deletes_group()
    {
        Group().Delete(Bob, Now).Error!.Type.ShouldBe(ErrorType.Forbidden);
    }
}
