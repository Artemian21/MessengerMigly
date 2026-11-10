using Messenger.Application.Abstractions;
using Messenger.Application.Summaries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Messenger.Infrastructure.Summaries;

/// <summary>Takes job ids off the queue and runs them, at most Ai:MaxConcurrency at a time.</summary>
public sealed class SummaryWorker(SummaryJobQueue queue, IServiceScopeFactory scopes, AiOptions options, ILogger<SummaryWorker> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.MaxConcurrency)).Select(_ => RunAsync(stoppingToken)));

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var jobId in queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SummaryJobProcessor>().ProcessAsync(jobId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Summary worker failed on job {JobId}", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // host is stopping
        }
    }
}
