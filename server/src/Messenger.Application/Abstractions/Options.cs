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

/// <summary>AI summary settings bound from configuration section "Ai" (plain object, registered as a singleton).</summary>
public sealed class AiOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

    /// <summary>Shared with the Python service (header X-Ai-Secret). Never logged.</summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>Longer than the Python service's own timeout (20 s) so its 504 reaches us first.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public int MaxMessages { get; set; } = 300;
    public int JobTtlMinutes { get; set; } = 15;
    public int MaxConcurrency { get; set; } = 2;
    public int QueueCapacity { get; set; } = 50;
}

/// <summary>Public base URL used in email links (App:PublicBaseUrl).</summary>
public sealed class AppOptions
{
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
}
