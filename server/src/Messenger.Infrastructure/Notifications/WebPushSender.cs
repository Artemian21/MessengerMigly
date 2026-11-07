using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Messenger.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DomainSubscription = Messenger.Domain.Users.PushSubscription;

namespace Messenger.Infrastructure.Notifications;

public sealed class VapidOptions
{
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    public bool IsConfigured => PublicKey.Length > 0 && PrivateKey.Length > 0 && Subject.Length > 0;
}

/// <summary>NTF-01: VAPID Web Push. Disabled (no-op) until all VAPID settings are present.</summary>
public sealed class WebPushSender(HttpClient http, IOptions<VapidOptions> options, ILogger<WebPushSender> logger) : IPushSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.Value.IsConfigured;

    public string? PublicKey => IsEnabled ? options.Value.PublicKey : null;

    public async Task<PushResult> SendAsync(DomainSubscription subscription, PushPayload payload, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return PushResult.Failed;
        }

        var o = options.Value;
        var client = new PushServiceClient(http)
        {
            DefaultAuthentication = new VapidAuthentication(o.PublicKey, o.PrivateKey) { Subject = o.Subject },
        };
        var target = new PushSubscription { Endpoint = subscription.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
        var message = new PushMessage(JsonSerializer.Serialize(payload, Json))
        {
            Topic = null,
            TimeToLive = 3600,
            Urgency = PushMessageUrgency.Normal,
        };

        try
        {
            await client.RequestPushMessageDeliveryAsync(target, message, ct);
            return PushResult.Sent;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushResult.Gone;
        }
        catch (Exception ex) when (ex is PushServiceClientException or HttpRequestException)
        {
            logger.LogWarning("Web push delivery failed: {Error}", ex.GetType().Name);
            return PushResult.Failed;
        }
    }
}
