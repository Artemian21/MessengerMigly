using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Application.Abstractions;
using Messenger.Application.Summaries;
using Messenger.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Messenger.Infrastructure.Summaries;

/// <summary>Calls the Python AI service (ai/). Never logs message text, the summary or the shared secret.</summary>
public sealed class HttpConversationSummarizer(HttpClient http, ILogger<HttpConversationSummarizer> logger) : IConversationSummarizer
{
    public const string SecretHeader = "X-Ai-Secret";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record WireMessage(string Author, DateTimeOffset SentAt, string Body);
    private sealed record WireRequest(string Language, IReadOnlyList<WireMessage> Messages);
    private sealed record WireResponse(string? Summary);

    public static void Configure(HttpClient client, AiOptions options)
    {
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        client.DefaultRequestHeaders.Remove(SecretHeader);
        client.DefaultRequestHeaders.Add(SecretHeader, options.SharedSecret);
    }

    public async Task<Result<string>> SummarizeAsync(string language, IReadOnlyList<SummaryMessage> messages, CancellationToken ct)
    {
        var request = new WireRequest(language, messages.Select(m => new WireMessage(m.Author, m.SentAt, m.Body)).ToList());
        try
        {
            using var response = await http.PostAsJsonAsync("v1/summarize", request, Json, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<WireResponse>(Json, ct);
                return string.IsNullOrWhiteSpace(body?.Summary) ? Failure(SummaryErrorCodes.Internal) : body.Summary;
            }

            logger.LogWarning("AI service answered {StatusCode}", (int)response.StatusCode);
            return Failure(response.StatusCode switch
            {
                HttpStatusCode.GatewayTimeout => SummaryErrorCodes.Timeout,
                HttpStatusCode.BadGateway => SummaryErrorCodes.ProviderUnavailable,
                _ => SummaryErrorCodes.Internal, // 401 (wrong shared secret), 422, anything unexpected
            });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("AI service did not answer in time");
            return Failure(SummaryErrorCodes.Timeout);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("AI service is unreachable ({Reason})", ex.GetType().Name);
            return Failure(SummaryErrorCodes.ProviderUnavailable);
        }
        catch (JsonException)
        {
            logger.LogWarning("AI service returned an unreadable response");
            return Failure(SummaryErrorCodes.Internal);
        }
    }

    private static Result<string> Failure(string code) => Result<string>.Failure(new Error(ErrorType.Conflict, code, "Summary failed."));
}
