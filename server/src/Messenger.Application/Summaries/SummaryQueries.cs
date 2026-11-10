using System.Globalization;
using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Application.Messages;
using Messenger.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Summaries;

/// <summary>Starts (or returns the already running) summary of what the caller has not read yet. A query, not a command:
/// nothing is written to the database, and a transaction retry must not create a second job.
/// <paramref name="AfterSequence"/>: summarize messages after this sequence instead of after "read up to". The client sends
/// the read position from when the chat was opened, because opening a chat marks it read. The join/clear floor still applies.</summary>
public sealed record StartSummaryQuery(Guid ConversationId, long? AfterSequence = null) : IQuery<Result<SummaryJobDto>>, IConversationMemberRequest;

public sealed class StartSummaryValidator : AbstractValidator<StartSummaryQuery>
{
    public StartSummaryValidator()
    {
        RuleFor(x => x.AfterSequence).GreaterThanOrEqualTo(0).When(x => x.AfterSequence is not null);
    }
}

public sealed class StartSummaryHandler(
    IApplicationDbContext db,
    MessageReader reader,
    ISummaryJobStore store,
    ISummaryJobQueue queue,
    ICurrentUser currentUser,
    AiOptions options) : IRequestHandler<StartSummaryQuery, Result<SummaryJobDto>>
{
    private static readonly SummaryJobDto Empty = new(null, "empty", null, null, 0, false, 0, 0);

    public async Task<Result<SummaryJobDto>> Handle(StartSummaryQuery request, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return Error.NotFound("Summary");
        }

        var me = currentUser.RequiredUserId;
        if (store.FindActive(me, request.ConversationId) is { } running)
        {
            return running.ToDto();
        }

        // Snapshot at request time: authorization and the read/clear/join watermarks apply now, not when the job runs.
        var floor = await ViewerFloor.GetAsync(db, request.ConversationId, me, ct);
        var from = request.AfterSequence ?? await db.Participants
            .Where(p => p.ConversationId == request.ConversationId && p.UserId == me)
            .Select(p => p.LastReadSequence)
            .FirstOrDefaultAsync(ct);
        var after = Math.Max(floor, from);

        // Same definition of "unread" as the conversation list: other people's messages, not deleted.
        var unread = db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.Sequence > after && m.SenderId != me && m.DeletedAt == null);
        var total = await unread.CountAsync(ct);
        if (total < Math.Max(1, options.MinMessages))
        {
            return Empty;
        }

        var ids = await unread.OrderByDescending(m => m.Sequence).Take(Math.Max(1, options.MaxMessages)).Select(m => m.Id).ToListAsync(ct);
        var rows = (await reader.ReadAsync(db.Messages.Where(m => ids.Contains(m.Id)), me, floor, ct))
            .Where(m => !m.IsDeleted && m.Body is not null)
            .ToList();
        if (rows.Count == 0)
        {
            return Empty;
        }

        var candidate = new SummaryJob(
            Guid.NewGuid(),
            me,
            request.ConversationId,
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "uk" ? "uk" : "en",
            SummaryStatus.Pending,
            rows.Select(m => new SummaryMessage(m.Sender.DisplayName, m.CreatedAt, m.Body!)).ToList(),
            rows.Count,
            total > rows.Count,
            rows.Min(m => m.Sequence),
            rows.Max(m => m.Sequence));

        var (job, created) = store.GetOrCreateActive(candidate);
        if (created && !queue.TryEnqueue(job.Id))
        {
            store.Fail(job.Id, SummaryErrorCodes.Internal);
            return Error.Conflict("ai_busy", "The assistant is busy. Try again in a minute.");
        }

        return job.ToDto();
    }
}

/// <summary>Current state of a job. Only its owner can see it; anything else is "not found".</summary>
public sealed record GetSummaryQuery(Guid ConversationId, Guid JobId) : IQuery<Result<SummaryJobDto>>, IConversationMemberRequest;

public sealed class GetSummaryHandler(ISummaryJobStore store, ICurrentUser currentUser, AiOptions options)
    : IRequestHandler<GetSummaryQuery, Result<SummaryJobDto>>
{
    public Task<Result<SummaryJobDto>> Handle(GetSummaryQuery request, CancellationToken ct)
    {
        var job = options.Enabled ? store.Get(request.JobId) : null;
        Result<SummaryJobDto> result = job is null || job.UserId != currentUser.RequiredUserId || job.ConversationId != request.ConversationId
            ? Error.NotFound("Summary")
            : job.ToDto();
        return Task.FromResult(result);
    }
}

/// <summary>What the client may offer. Lets it hide the button when the feature is off and use the server's minimum.</summary>
public sealed record AiConfigDto(bool SummaryEnabled, int MinMessages);

public sealed record GetAiConfigQuery : IQuery<Result<AiConfigDto>>;

public sealed class GetAiConfigHandler(AiOptions options) : IRequestHandler<GetAiConfigQuery, Result<AiConfigDto>>
{
    public Task<Result<AiConfigDto>> Handle(GetAiConfigQuery request, CancellationToken ct) =>
        Task.FromResult(Result.Success(new AiConfigDto(options.Enabled, Math.Max(1, options.MinMessages))));
}
