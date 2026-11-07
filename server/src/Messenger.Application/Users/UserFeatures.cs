using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Auth;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Users;

public sealed record GetMeQuery : IQuery<Result<MeDto>>;

public sealed class GetMeHandler(IApplicationDbContext db, ICurrentUser currentUser) : IRequestHandler<GetMeQuery, Result<MeDto>>
{
    public async Task<Result<MeDto>> Handle(GetMeQuery request, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUser.RequiredUserId, ct);
        return user is null || user.Status == UserStatus.Deleted ? Error.Unauthorized() : SessionIssuer.ToMe(user);
    }
}

// USR-04
public sealed record UpdateProfileCommand(string DisplayName) : ICommand<Result<MeDto>>;

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator() => RuleFor(x => x.DisplayName).ValidDisplayName();
}

public sealed class UpdateProfileHandler(IUserRepository users, ICurrentUser currentUser) : IRequestHandler<UpdateProfileCommand, Result<MeDto>>
{
    public async Task<Result<MeDto>> Handle(UpdateProfileCommand request, CancellationToken ct)
    {
        var user = await users.GetAsync(currentUser.RequiredUserId, ct);
        if (user is null)
        {
            return Error.Unauthorized();
        }

        user.ChangeDisplayName(request.DisplayName);
        return SessionIssuer.ToMe(user);
    }
}

// USR-02: usernames change freely and are unique case-insensitively among non-deleted users.
public sealed record ChangeUsernameCommand(string Username) : ICommand<Result<MeDto>>, IRetryOnUniqueViolation;

public sealed class ChangeUsernameValidator : AbstractValidator<ChangeUsernameCommand>
{
    public ChangeUsernameValidator() => RuleFor(x => x.Username).ValidUsername();
}

public sealed class ChangeUsernameHandler(IUserRepository users, ICurrentUser currentUser) : IRequestHandler<ChangeUsernameCommand, Result<MeDto>>
{
    public async Task<Result<MeDto>> Handle(ChangeUsernameCommand request, CancellationToken ct)
    {
        var user = await users.GetAsync(currentUser.RequiredUserId, ct);
        if (user is null)
        {
            return Error.Unauthorized();
        }

        var username = InputRules.NormalizeUsername(request.Username);
        if (await users.UsernameTakenAsync(username, user.Id, ct))
        {
            return AuthErrors.UsernameTaken;
        }

        user.ChangeUsername(username);
        return SessionIssuer.ToMe(user);
    }
}

// USR-05 / P-15
public sealed record DeleteAccountCommand(string? Password) : ICommand<Result>;

public sealed class DeleteAccountHandler(IUserRepository users, IConversationRepository conversations, IAuthTokenRepository tokens,
    IPushSubscriptionRepository push, IPasswordHasher hasher, ICurrentUser currentUser, IAuditLogger audit, IClock clock)
    : IRequestHandler<DeleteAccountCommand, Result>
{
    public async Task<Result> Handle(DeleteAccountCommand request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var user = await users.GetAsync(currentUser.RequiredUserId, ct);
        if (user is null)
        {
            return Error.Unauthorized();
        }

        if (user.PasswordHash is not null && (request.Password is null || !hasher.Verify(request.Password, user.PasswordHash)))
        {
            return Error.Validation("password", "Password is incorrect.");
        }

        foreach (var group in await conversations.GetGroupsOfUserAsync(user.Id, ct))
        {
            group.Leave(user.Id, now);
        }

        user.Delete(now);
        await tokens.RevokeAllForUserAsync(user.Id, null, now, ct);
        await push.RemoveAllForUserAsync(user.Id, ct);
        audit.Record("user.deleted", "user", user.Id.ToString());
        return Result.Success();
    }
}

// USR-03: prefix search to start a chat; excludes deleted, suspended and unverified users.
public sealed record SearchUsersQuery(string? Q, int? Limit, int? Offset) : IQuery<Result<PagedResult<UserSummaryDto>>>;

public sealed class SearchUsersValidator : AbstractValidator<SearchUsersQuery>
{
    public SearchUsersValidator() =>
        RuleFor(x => x.Q).Must(q => InputRules.Normalize(q).Length is >= 2 and <= 64)
            .WithMessage("Search text must be 2-64 characters.")
            .Must(q => !InputRules.HasForbiddenControlChars(InputRules.Normalize(q), allowLineBreaks: false))
            .WithMessage("Search text contains forbidden control characters.");
}

public sealed class SearchUsersHandler(IApplicationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<SearchUsersQuery, Result<PagedResult<UserSummaryDto>>>
{
    public async Task<Result<PagedResult<UserSummaryDto>>> Handle(SearchUsersQuery request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var term = InputRules.Normalize(request.Q);
        var pattern = LikePattern.Prefix(term.TrimStart('@').ToLowerInvariant());
        var limit = Paging.Limit(request.Limit);
        var offset = Paging.Offset(request.Offset);

        var query = db.Users.AsNoTracking().Where(u =>
            u.Id != me && u.Status == UserStatus.Active && u.EmailConfirmed && !u.NeedsProfile &&
            (EF.Functions.Like(u.UsernameNormalized, pattern, LikePattern.Escape) ||
             EF.Functions.Like(u.DisplayName.ToLower(), pattern, LikePattern.Escape)));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.UsernameNormalized)
            .Skip(offset).Take(limit)
            .Select(u => new UserSummaryDto(u.Id, u.Username, u.DisplayName))
            .ToListAsync(ct);
        return PagedResult<UserSummaryDto>.Create(items, total, offset, limit);
    }
}

// USR-06
public sealed record GetUserQuery(Guid UserId) : IQuery<Result<PublicUserDto>>;

public sealed class GetUserHandler(IApplicationDbContext db, ICurrentUser currentUser, IPresenceTracker presence)
    : IRequestHandler<GetUserQuery, Result<PublicUserDto>>
{
    public async Task<Result<PublicUserDto>> Handle(GetUserQuery request, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null || (user.Status != UserStatus.Active && user.Status != UserStatus.Deleted))
        {
            return Error.NotFound("User");
        }

        var shared = user.Id == currentUser.RequiredUserId ||
                     await PresenceRules.SharesConversationAsync(db, currentUser.RequiredUserId, user.Id, ct);
        return PresenceRules.ToPublic(user, shared, presence);
    }
}

public static class PresenceRules
{
    /// <summary>PRS-04: presence is visible only to users who share an active conversation.</summary>
    public static Task<bool> SharesConversationAsync(IApplicationDbContext db, Guid me, Guid other, CancellationToken ct) =>
        (from mine in db.Participants
         join theirs in db.Participants on mine.ConversationId equals theirs.ConversationId
         where mine.UserId == me && mine.LeftAt == null && theirs.UserId == other && theirs.LeftAt == null
         select mine.ConversationId).AnyAsync(ct);

    public static async Task<IReadOnlyList<Guid>> FilterSharedAsync(IApplicationDbContext db, Guid me, IReadOnlyCollection<Guid> others, CancellationToken ct) =>
        await (from mine in db.Participants
               join theirs in db.Participants on mine.ConversationId equals theirs.ConversationId
               where mine.UserId == me && mine.LeftAt == null && theirs.LeftAt == null && others.Contains(theirs.UserId)
               select theirs.UserId).Distinct().ToListAsync(ct);

    public static PublicUserDto ToPublic(User user, bool showPresence, IPresenceTracker presence)
    {
        var deleted = user.Status == UserStatus.Deleted;
        return new PublicUserDto(user.Id, user.Username, user.DisplayName, deleted,
            showPresence && !deleted ? presence.IsOnline(user.Id) : null,
            showPresence && !deleted ? user.LastSeenAt : null);
    }
}

public sealed record PresenceDto(Guid UserId, bool Online, DateTimeOffset? LastSeenAt);

/// <summary>Hub WatchPresence: snapshot for the requested users the caller may watch (max 100, PRS-04).</summary>
public sealed record GetPresenceQuery(IReadOnlyCollection<Guid> UserIds) : IQuery<Result<IReadOnlyList<PresenceDto>>>;

public sealed class GetPresenceHandler(IApplicationDbContext db, ICurrentUser currentUser, IPresenceTracker presence)
    : IRequestHandler<GetPresenceQuery, Result<IReadOnlyList<PresenceDto>>>
{
    public async Task<Result<IReadOnlyList<PresenceDto>>> Handle(GetPresenceQuery request, CancellationToken ct)
    {
        var ids = request.UserIds.Distinct().Take(100).ToList();
        var allowed = await PresenceRules.FilterSharedAsync(db, currentUser.RequiredUserId, ids, ct);
        var rows = await db.Users.AsNoTracking()
            .Where(u => allowed.Contains(u.Id) && u.Status != UserStatus.Deleted)
            .Select(u => new { u.Id, u.LastSeenAt })
            .ToListAsync(ct);
        IReadOnlyList<PresenceDto> items = rows.Select(r => new PresenceDto(r.Id, presence.IsOnline(r.Id), r.LastSeenAt)).ToList();
        return Result.Success(items);
    }
}

/// <summary>LIKE helpers: user input is escaped so % and _ are literal (API-14: always parameterized).</summary>
public static class LikePattern
{
    public const string Escape = "\\";

    public static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static string Prefix(string value) => EscapeValue(value) + "%";

    public static string Contains(string value) => "%" + EscapeValue(value) + "%";
}
