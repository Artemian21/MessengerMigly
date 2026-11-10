using Messenger.Application.Abstractions;
using Messenger.Application.Summaries;

namespace Messenger.Infrastructure.Summaries;

/// <summary>Summary jobs in memory: the app runs as a single instance (like presence), and a restart only loses
/// summaries that were still being generated. Entries expire Ai:JobTtlMinutes after their last change.</summary>
public sealed class InMemorySummaryJobStore(IClock clock, AiOptions options) : ISummaryJobStore
{
    private sealed record Entry(SummaryJob Job, DateTimeOffset ExpiresAt);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _jobs = [];

    public SummaryJob? FindActive(Guid userId, Guid conversationId)
    {
        lock (_gate)
        {
            Prune();
            return Active(userId, conversationId);
        }
    }

    public (SummaryJob Job, bool Created) GetOrCreateActive(SummaryJob candidate)
    {
        lock (_gate)
        {
            Prune();
            if (Active(candidate.UserId, candidate.ConversationId) is { } existing)
            {
                return (existing, false);
            }

            _jobs[candidate.Id] = new Entry(candidate, ExpiryFromNow());
            return (candidate, true);
        }
    }

    public SummaryJob? Get(Guid jobId)
    {
        lock (_gate)
        {
            Prune();
            return _jobs.TryGetValue(jobId, out var entry) ? entry.Job : null;
        }
    }

    // Finished jobs drop the message texts: nothing sensitive stays in memory longer than needed.
    public void Complete(Guid jobId, string summary) =>
        Update(jobId, job => job with { Status = SummaryStatus.Ready, Summary = summary, Messages = [] });

    public void Fail(Guid jobId, string errorCode) =>
        Update(jobId, job => job with { Status = SummaryStatus.Failed, ErrorCode = errorCode, Messages = [] });

    private void Update(Guid jobId, Func<SummaryJob, SummaryJob> change)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
            {
                _jobs[jobId] = new Entry(change(entry.Job), ExpiryFromNow());
            }
        }
    }

    private SummaryJob? Active(Guid userId, Guid conversationId) =>
        _jobs.Values
            .Select(e => e.Job)
            .FirstOrDefault(j => j.UserId == userId && j.ConversationId == conversationId && j.Status == SummaryStatus.Pending);

    private DateTimeOffset ExpiryFromNow() => clock.UtcNow.AddMinutes(options.JobTtlMinutes);

    private void Prune()
    {
        var now = clock.UtcNow;
        foreach (var id in _jobs.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToList())
        {
            _jobs.Remove(id);
        }
    }
}
