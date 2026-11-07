using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Conversations;

// CNV-04
public sealed record CreateGroupCommand(string Title, IReadOnlyList<Guid> MemberIds) : ICommand<Result<ConversationDto>>;

public sealed class CreateGroupValidator : AbstractValidator<CreateGroupCommand>
{
    public CreateGroupValidator()
    {
        RuleFor(x => x.Title).ValidGroupTitle();
        RuleFor(x => x.MemberIds).NotNull().Must(ids => ids.Count <= Conversation.MaxGroupMembers)
            .WithMessage($"A group can have at most {Conversation.MaxGroupMembers} members.");
    }
}

public sealed class CreateGroupHandler(IConversationRepository conversations, IApplicationDbContext db, IUnitOfWork unitOfWork,
    ConversationReader reader, ICurrentUser currentUser, IClock clock) : IRequestHandler<CreateGroupCommand, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(CreateGroupCommand request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var memberIds = request.MemberIds.Where(id => id != me).Distinct().ToList();
        if (await MemberRules.FindUnavailableAsync(db, memberIds, ct) is { } error)
        {
            return error;
        }

        var created = Conversation.CreateGroup(me, request.Title, memberIds, clock.UtcNow);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        conversations.Add(created.Value);
        await unitOfWork.SaveChangesAsync(ct);
        return (await reader.ReadAsync(me, created.Value.Id, ct))[0];
    }
}

// CNV-10
public sealed record RenameGroupCommand(Guid ConversationId, string Title) : ICommand<Result<ConversationDto>>, IConversationMemberRequest;

public sealed class RenameGroupValidator : AbstractValidator<RenameGroupCommand>
{
    public RenameGroupValidator() => RuleFor(x => x.Title).ValidGroupTitle();
}

public sealed class RenameGroupHandler(IConversationRepository conversations, IUnitOfWork unitOfWork, ConversationReader reader,
    ICurrentUser currentUser) : IRequestHandler<RenameGroupCommand, Result<ConversationDto>>
{
    public async Task<Result<ConversationDto>> Handle(RenameGroupCommand request, CancellationToken ct)
    {
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        var result = conversation!.Rename(currentUser.RequiredUserId, request.Title);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        await unitOfWork.SaveChangesAsync(ct);
        return (await reader.ReadAsync(currentUser.RequiredUserId, conversation.Id, ct))[0];
    }
}

// CNV-08
public sealed record AddMembersCommand(Guid ConversationId, IReadOnlyList<Guid> UserIds) : ICommand<Result>, IConversationMemberRequest;

public sealed class AddMembersValidator : AbstractValidator<AddMembersCommand>
{
    public AddMembersValidator() =>
        RuleFor(x => x.UserIds).NotEmpty().WithMessage("Choose at least one user.")
            .Must(ids => ids.Count <= 50).WithMessage("Add at most 50 users at once.");
}

public sealed class AddMembersHandler(IConversationRepository conversations, IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<AddMembersCommand, Result>
{
    public async Task<Result> Handle(AddMembersCommand request, CancellationToken ct)
    {
        if (await MemberRules.FindUnavailableAsync(db, request.UserIds.Distinct().ToList(), ct) is { } error)
        {
            return error;
        }

        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        return conversation!.AddMembers(currentUser.RequiredUserId, request.UserIds, clock.UtcNow);
    }
}

public sealed record RemoveMemberCommand(Guid ConversationId, Guid UserId) : ICommand<Result>, IConversationMemberRequest;

public sealed class RemoveMemberHandler(IConversationRepository conversations, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<RemoveMemberCommand, Result>
{
    public async Task<Result> Handle(RemoveMemberCommand request, CancellationToken ct)
    {
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        return conversation!.RemoveMember(currentUser.RequiredUserId, request.UserId, clock.UtcNow);
    }
}

public sealed record ChangeMemberRoleCommand(Guid ConversationId, Guid UserId, string Role) : ICommand<Result>, IConversationMemberRequest;

public sealed class ChangeMemberRoleValidator : AbstractValidator<ChangeMemberRoleCommand>
{
    public ChangeMemberRoleValidator() =>
        RuleFor(x => x.Role).Must(r => r is "Admin" or "Member").WithMessage("Role must be 'Admin' or 'Member'.");
}

public sealed class ChangeMemberRoleHandler(IConversationRepository conversations, ICurrentUser currentUser)
    : IRequestHandler<ChangeMemberRoleCommand, Result>
{
    public async Task<Result> Handle(ChangeMemberRoleCommand request, CancellationToken ct)
    {
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        return conversation!.ChangeRole(currentUser.RequiredUserId, request.UserId, Enum.Parse<ParticipantRole>(request.Role));
    }
}

// CNV-06
public sealed record LeaveConversationCommand(Guid ConversationId) : ICommand<Result>, IConversationMemberRequest;

public sealed class LeaveConversationHandler(IConversationRepository conversations, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<LeaveConversationCommand, Result>
{
    public async Task<Result> Handle(LeaveConversationCommand request, CancellationToken ct)
    {
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        return conversation!.Leave(currentUser.RequiredUserId, clock.UtcNow);
    }
}

// CNV-03
public sealed record ClearConversationCommand(Guid ConversationId) : ICommand<Result>, IConversationMemberRequest;

public sealed class ClearConversationHandler(IConversationRepository conversations, ICurrentUser currentUser)
    : IRequestHandler<ClearConversationCommand, Result>
{
    public async Task<Result> Handle(ClearConversationCommand request, CancellationToken ct)
    {
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        return conversation!.Clear(currentUser.RequiredUserId);
    }
}

// CNV-07
public sealed record DeleteGroupCommand(Guid ConversationId) : ICommand<Result>, IConversationMemberRequest;

public sealed class DeleteGroupHandler(IConversationRepository conversations, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<DeleteGroupCommand, Result>
{
    public async Task<Result> Handle(DeleteGroupCommand request, CancellationToken ct)
    {
        var conversation = await conversations.GetWithParticipantsAsync(request.ConversationId, ct);
        return conversation!.Delete(currentUser.RequiredUserId, clock.UtcNow);
    }
}

internal static class MemberRules
{
    /// <summary>Only active, verified, onboarded users can be added to groups.</summary>
    public static async Task<Error?> FindUnavailableAsync(IApplicationDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return null;
        }

        var available = await db.Users.AsNoTracking()
            .CountAsync(u => userIds.Contains(u.Id) && u.Status == UserStatus.Active && u.EmailConfirmed, ct);
        return available == userIds.Count ? null : Error.Validation("memberIds", "Some users do not exist or cannot be added.");
    }
}
