using System.Threading.Channels;
using Messenger.Application.Abstractions;

namespace Messenger.Infrastructure.Summaries;

/// <summary>Bounded in-memory queue of job ids; the worker reads it. A full queue rejects instead of waiting.</summary>
public sealed class SummaryJobQueue(AiOptions options) : ISummaryJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(Math.Max(1, options.QueueCapacity)) { FullMode = BoundedChannelFullMode.Wait });

    public bool TryEnqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public ChannelReader<Guid> Reader => _channel.Reader;
}
