using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Application.Users;
using Messenger.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Messages;

/// <summary>MSG-07 / P-17: case-insensitive search (ILIKE, trigram index) over messages the caller can currently see:
/// active membership, after join, after clear, not deleted.</summary>
public sealed record SearchMessagesQuery(string? Q, Guid? ConversationId, int? Limit, int? Offset) : IQuery<Result<PagedResult<SearchHitDto>>>;

public sealed class SearchMessagesValidator : AbstractValidator<SearchMessagesQuery>
{
    public SearchMessagesValidator() =>
        RuleFor(x => x.Q).Must(q => InputRules.Normalize(q).Length is >= 2 and <= 200)
            .WithMessage("Search text must be 2-200 characters.")
            .Must(q => !InputRules.HasForbiddenControlChars(InputRules.Normalize(q), allowLineBreaks: false))
            .WithMessage("Search text contains forbidden control characters.");
}

public sealed class SearchMessagesHandler(IApplicationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<SearchMessagesQuery, Result<PagedResult<SearchHitDto>>>
{
    private const int SnippetRadius = 60;

    public async Task<Result<PagedResult<SearchHitDto>>> Handle(SearchMessagesQuery request, CancellationToken ct)
    {
        var me = currentUser.RequiredUserId;
        var term = InputRules.Normalize(request.Q);
        var pattern = LikePattern.Contains(term.ToLowerInvariant());
        var limit = Paging.Limit(request.Limit);
        var offset = Paging.Offset(request.Offset);

        var query =
            from m in db.Messages
            join p in db.Participants on m.ConversationId equals p.ConversationId
            join c in db.Conversations on m.ConversationId equals c.Id
            join u in db.Users on m.SenderId equals u.Id
            where p.UserId == me && p.LeftAt == null
                  && m.Sequence >= p.VisibleFromSequence && m.Sequence > p.ClearedUpToSequence
                  && m.DeletedAt == null
                  && (request.ConversationId == null || m.ConversationId == request.ConversationId)
                  && EF.Functions.Like(m.Body!.ToLower(), pattern, LikePattern.Escape)
            select new { m, c.Title, u.Username, u.DisplayName, u.Status };

        var total = await query.CountAsync(ct);
        var rows = await query.AsNoTracking()
            .OrderByDescending(x => x.m.CreatedAt).ThenByDescending(x => x.m.Sequence)
            .Skip(offset).Take(limit)
            .Select(x => new
            {
                x.m.Id, x.m.ConversationId, x.m.Sequence, x.m.SenderId, x.m.Body, x.m.CreatedAt,
                x.m.SenderUsernameSnapshot, x.m.SenderDisplayNameSnapshot, x.Title, x.Username, x.DisplayName, x.Status,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new SearchHitDto(r.Id, r.ConversationId, r.Title, r.Sequence,
                MessageReader.Sender(r.SenderId, r.Username, r.DisplayName, r.Status, r.SenderUsernameSnapshot, r.SenderDisplayNameSnapshot),
                Snippet(r.Body!, term), r.CreatedAt))
            .ToList();
        return PagedResult<SearchHitDto>.Create(items, total, offset, limit);
    }

    internal static string Snippet(string body, string term)
    {
        var index = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || body.Length <= SnippetRadius * 2)
        {
            return body.Length <= SnippetRadius * 2 ? body : body[..(SnippetRadius * 2)] + "…";
        }

        var start = Math.Max(0, index - SnippetRadius);
        var end = Math.Min(body.Length, index + term.Length + SnippetRadius);
        return (start > 0 ? "…" : "") + body[start..end] + (end < body.Length ? "…" : "");
    }
}
