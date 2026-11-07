using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using Messenger.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Messenger.Infrastructure.Notifications;

public sealed class EmailOptions
{
    /// <summary>Mailpit (SMTP, local), Brevo (HTTP API, production) or None (emails are dropped, links are not logged).</summary>
    public string Provider { get; set; } = "Mailpit";
    public string From { get; set; } = "Migly <no-reply@migly.local>";
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public string BrevoApiKey { get; set; } = string.Empty;
}

/// <summary>Plain SMTP without auth: intended for Mailpit in docker-compose.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = false };
        using var mail = new MailMessage
        {
            From = new MailAddress(ParseAddress(o.From).Email, ParseAddress(o.From).Name),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        mail.To.Add(message.To);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, Encoding.UTF8, "text/plain"));
        await client.SendMailAsync(mail, ct);
    }

    internal static (string Name, string Email) ParseAddress(string from)
    {
        var start = from.IndexOf('<');
        return start < 0 ? ("Migly", from.Trim()) : (from[..start].Trim(), from[(start + 1)..].TrimEnd('>', ' '));
    }
}

/// <summary>Brevo transactional email HTTP API (free tier: 300 emails/day).</summary>
public sealed class BrevoEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<BrevoEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var (name, email) = SmtpEmailSender.ParseAddress(options.Value.From);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
        {
            Content = JsonContent.Create(new
            {
                sender = new { name, email },
                to = new[] { new { email = message.To } },
                subject = message.Subject,
                htmlContent = message.HtmlBody,
                textContent = message.TextBody,
            }),
        };
        request.Headers.Add("api-key", options.Value.BrevoApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Brevo rejected an email with status {Status}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
    }
}

public sealed class NullEmailSender(ILogger<NullEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogWarning("Email provider is 'None'; dropped email with subject {Subject}", message.Subject);
        return Task.CompletedTask;
    }
}
