using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Application.Users;
using Messenger.Domain.Common;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Admin;

// RBAC-01: system admins manage accounts but never read private message content.
public sealed record AdminListUsersQuery(string? Q, int? Limit, int? Offset) : IQuery<Result<PagedResult<AdminUserDto>>>, IAdminRequest;

public sealed class AdminListUsersValidator : AbstractValidator<AdminListUsersQuery>
{
    public AdminListUsersValidator() =>
        RuleFor(x => x.Q).Must(q => q is null || !InputRules.HasForbiddenControlChars(InputRules.Normalize(q), allowLineBreaks: false))
            .WithMessage("Search text contains forbidden control characters.");
}

public sealed class AdminListUsersHandler(IApplicationDbContext db) : IRequestHandler<AdminListUsersQuery, Result<PagedResult<AdminUserDto>>>
{
    public async Task<Result<PagedResult<AdminUserDto>>> Handle(AdminListUsersQuery request, CancellationToken ct)
    {
        var limit = Paging.Limit(request.Limit);
        var offset = Paging.Offset(request.Offset);
        var query = db.Users.AsNoTracking().Where(u => u.Status != UserStatus.Deleted);
        var term = InputRules.Normalize(request.Q);
        if (term.Length > 0)
        {
            var pattern = LikePattern.Contains(term.ToLowerInvariant());
            query = query.Where(u => EF.Functions.Like(u.UsernameNormalized, pattern, LikePattern.Escape)
                                     || EF.Functions.Like(u.EmailNormalized.ToLower(), pattern, LikePattern.Escape)
                                     || EF.Functions.Like(u.DisplayName.ToLower(), pattern, LikePattern.Escape));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(u => u.CreatedAt).Skip(offset).Take(limit)
            .Select(u => new AdminUserDto(u.Id, u.Email, u.Username, u.DisplayName, u.Role.ToString(), u.Status.ToString(),
                u.EmailConfirmed, u.CreatedAt, u.LastSeenAt))
            .ToListAsync(ct);
        return PagedResult<AdminUserDto>.Create(items, total, offset, limit);
    }
}

public sealed record SuspendUserCommand(Guid UserId) : ICommand<Result>, IAdminRequest;

public sealed class SuspendUserHandler(IUserRepository users, IAuthTokenRepository tokens, ICurrentUser currentUser, IAuditLogger audit, IClock clock)
    : IRequestHandler<SuspendUserCommand, Result>
{
    public async Task<Result> Handle(SuspendUserCommand request, CancellationToken ct)
    {
        if (request.UserId == currentUser.RequiredUserId)
        {
            return Error.BadRequest("cannot_suspend_self", "You cannot suspend your own account.");
        }

        var user = await users.GetAsync(request.UserId, ct);
        if (user is null || user.Status == UserStatus.Deleted)
        {
            return Error.NotFound("User");
        }

        if (user.Role == SystemRole.Admin)
        {
            return Error.Forbidden("Administrators cannot be suspended.");
        }

        user.Suspend();
        await tokens.RevokeAllForUserAsync(user.Id, null, clock.UtcNow, ct);
        audit.Record("admin.user_suspended", "user", user.Id.ToString());
        return Result.Success();
    }
}

public sealed record UnsuspendUserCommand(Guid UserId) : ICommand<Result>, IAdminRequest;

public sealed class UnsuspendUserHandler(IUserRepository users, IAuditLogger audit) : IRequestHandler<UnsuspendUserCommand, Result>
{
    public async Task<Result> Handle(UnsuspendUserCommand request, CancellationToken ct)
    {
        var user = await users.GetAsync(request.UserId, ct);
        if (user is null || user.Status == UserStatus.Deleted)
        {
            return Error.NotFound("User");
        }

        user.Unsuspend();
        audit.Record("admin.user_unsuspended", "user", user.Id.ToString());
        return Result.Success();
    }
}

// AUD-01
public sealed record ListAuditLogQuery(int? Limit, int? Offset) : IQuery<Result<PagedResult<AuditLogDto>>>, IAdminRequest;

public sealed class ListAuditLogHandler(IApplicationDbContext db) : IRequestHandler<ListAuditLogQuery, Result<PagedResult<AuditLogDto>>>
{
    public async Task<Result<PagedResult<AuditLogDto>>> Handle(ListAuditLogQuery request, CancellationToken ct)
    {
        var limit = Paging.Limit(request.Limit);
        var offset = Paging.Offset(request.Offset);
        var total = await db.AuditLogs.CountAsync(ct);
        var items = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt).Skip(offset).Take(limit)
            .Select(a => new AuditLogDto(a.Id, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.DataJson, a.Ip, a.CreatedAt))
            .ToListAsync(ct);
        return PagedResult<AuditLogDto>.Create(items, total, offset, limit);
    }
}
