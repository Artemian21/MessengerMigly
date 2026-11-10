using System.Globalization;
using System.Net;
using System.Resources;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;

namespace Messenger.Application.Auth;

/// <summary>Sends verification / reset emails after the token row is committed, in the request language
/// (Resources/EmailTexts.*.resx: en, uk). Links point to the SPA.</summary>
public sealed class AuthEmailHandlers(IEmailSender sender, AppOptions app)
    : INotificationHandler<DomainEventNotification<VerificationEmailRequested>>,
      INotificationHandler<DomainEventNotification<PasswordResetRequested>>
{
    private static readonly ResourceManager Texts = new("Messenger.Application.Resources.EmailTexts", typeof(AuthEmailHandlers).Assembly);

    public Task Handle(DomainEventNotification<VerificationEmailRequested> notification, CancellationToken ct)
    {
        var e = notification.Event;
        var link = $"{app.PublicBaseUrl.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(e.RawToken)}";
        return sender.SendAsync(Build(e.Email, Text("VerifySubject"), e.DisplayName, Text("VerifyIntro"), link, Text("VerifyAction"),
            Text("VerifyFooter")), ct);
    }

    public Task Handle(DomainEventNotification<PasswordResetRequested> notification, CancellationToken ct)
    {
        var e = notification.Event;
        var link = $"{app.PublicBaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(e.RawToken)}";
        return sender.SendAsync(Build(e.Email, Text("ResetSubject"), e.DisplayName, Text("ResetIntro"), link, Text("ResetAction"),
            Text("ResetFooter")), ct);
    }

    internal static string Text(string key) => Texts.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private static EmailMessage Build(string to, string subject, string name, string intro, string link, string action, string footer)
    {
        var greeting = string.Format(CultureInfo.CurrentUICulture, Text("Greeting"), name);
        var signature = Text("Signature");
        var text = $"{greeting}\n\n{intro}\n{link}\n\n{footer}\n\n{signature}";
        var html = $"""
            <p>{WebUtility.HtmlEncode(greeting)}</p>
            <p>{WebUtility.HtmlEncode(intro)}</p>
            <p><a href="{WebUtility.HtmlEncode(link)}">{WebUtility.HtmlEncode(action)}</a></p>
            <p style="color:#666">{WebUtility.HtmlEncode(footer)}</p>
            <p>{WebUtility.HtmlEncode(signature)}</p>
            """;
        return new EmailMessage(to, subject, text, html);
    }
}
