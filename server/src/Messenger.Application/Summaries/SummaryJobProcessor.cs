using Messenger.Application.Abstractions;
using Messenger.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Messenger.Application.Summaries;

/// <summary>Runs one summary job: asks the AI service, stores the result, tells the requester's devices.
/// Logs ids, statuses and counts only; never message text or the summary.</summary>
public sealed class SummaryJobProcessor(
    ISummaryJobStore store,
    IConversationSummarizer summarizer,
    IRealtimeNotifier notifier,
    ILogger<SummaryJobProcessor> logger)
{
    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = store.Get(jobId);
        if (job is null || job.Status != SummaryStatus.Pending)
        {
            return;
        }

        Result<string> result;
        try
        {
            result = await summarizer.SummarizeAsync(job.Language, job.Messages, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // shutting down: jobs live in memory only, nothing to clean up
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Summary job {JobId} crashed", jobId);
            result = Result<string>.Failure(new Error(ErrorType.Conflict, SummaryErrorCodes.Internal, "Summary failed."));
        }

        if (result.IsSuccess)
        {
            store.Complete(jobId, result.Value);
            logger.LogInformation("Summary job {JobId} ready for {Messages} message(s)", jobId, job.MessageCount);
            await NotifyAsync(job, "ready", result.Value, null, ct);
        }
        else
        {
            var code = result.Error!.Code;
            store.Fail(jobId, code);
            logger.LogWarning("Summary job {JobId} failed: {ErrorCode}", jobId, code);
            await NotifyAsync(job, "failed", null, code, ct);
        }
    }

    private async Task NotifyAsync(SummaryJob job, string status, string? summary, string? errorCode, CancellationToken ct)
    {
        try
        {
            await notifier.SummaryCompletedAsync(job.UserId, job.ConversationId, job.Id, status, summary, errorCode, ct);
        }
        catch (Exception ex)
        {
            // The client can still fetch the result with GET; a lost event is never fatal.
            logger.LogWarning(ex, "Could not push SummaryCompleted for job {JobId}", job.Id);
        }
    }
}
