namespace Messenger.Application.Abstractions;

/// <summary>Auth settings bound from configuration section "Auth" (plain object, registered as a singleton).</summary>
public sealed class AuthOptions
{
    public int RefreshTtlDays { get; set; } = 21;
    public int RefreshGraceSeconds { get; set; } = 20;
    public int EmailVerificationTtlHours { get; set; } = 24;
    public int PasswordResetTtlMinutes { get; set; } = 45;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public bool RequireEmailVerification { get; set; } = true;
}

/// <summary>Public base URL used in email links (App:PublicBaseUrl).</summary>
public sealed class AppOptions
{
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
}
