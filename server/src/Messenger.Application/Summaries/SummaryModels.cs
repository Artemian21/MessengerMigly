namespace Messenger.Application.Summaries;

public enum SummaryStatus { Pending, Ready, Failed }

/// <summary>One unread message as the AI service sees it. Only what the summary needs; never ids or attachments.</summary>
public sealed record SummaryMessage(string Author, DateTimeOffset SentAt, string Body);

/// <summary>Error codes of a failed job. The client maps them to its own texts, so no server text is involved.</summary>
public static class SummaryErrorCodes
{
    public const string ProviderUnavailable = "provider_unavailable";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
}

/// <summary>Immutable snapshot of a summary job. <see cref="Messages"/> are dropped as soon as the job finishes.</summary>
public sealed record SummaryJob(
    Guid Id,
    Guid UserId,
    Guid ConversationId,
    string Language,
    SummaryStatus Status,
    IReadOnlyList<SummaryMessage> Messages,
    int MessageCount,
    bool Truncated,
    long FromSequence,
    long ToSequence,
    string? Summary = null,
    string? ErrorCode = null)
{
    public SummaryJobDto ToDto() =>
        new(Id, Status.ToString().ToLowerInvariant(), Summary, ErrorCode, MessageCount, Truncated, FromSequence, ToSequence);
}

/// <summary>Status is pending | ready | failed, or empty (nothing unread; no job was created and JobId is null).</summary>
public sealed record SummaryJobDto(
    Guid? JobId,
    string Status,
    string? Summary,
    string? ErrorCode,
    int MessageCount,
    bool Truncated,
    long FromSequence,
    long ToSequence);
