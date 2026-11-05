using Messenger.Domain.Common;

namespace Messenger.Domain.Users;

public enum UserStatus { Active, Suspended, Deleted }

public enum SystemRole { User, Admin }

public sealed class User : Entity
{
    public const string DeletedDisplayName = "Deleted account";

    private User() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string EmailNormalized { get; private set; } = null!;
    public bool EmailConfirmed { get; private set; }
    public string? PasswordHash { get; private set; }
    public string Username { get; private set; } = null!;
    public string UsernameNormalized { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public SystemRole Role { get; private set; }
    public UserStatus Status { get; private set; }
    public bool NeedsProfile { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockoutUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }

    public bool IsActive => Status == UserStatus.Active;

    public static User Register(string email, string username, string displayName, string? passwordHash, DateTimeOffset now)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(now),
            Email = InputRules.Normalize(email),
            EmailNormalized = InputRules.NormalizeEmail(email),
            Username = InputRules.NormalizeUsername(username),
            UsernameNormalized = InputRules.NormalizeUsername(username),
            DisplayName = InputRules.Normalize(displayName),
            PasswordHash = passwordHash,
            Role = SystemRole.User,
            Status = UserStatus.Active,
            CreatedAt = now,
        };
        user.Raise(new UserRegistered(user.Id));
        return user;
    }

    public void ConfirmEmail() => EmailConfirmed = true;

    public void MarkNeedsProfile() => NeedsProfile = true;

    public void PromoteToAdmin() => Role = SystemRole.Admin;

    public bool IsLockedOut(DateTimeOffset now) => LockoutUntil is { } until && until > now;

    /// <summary>AUTH-10: after <paramref name="maxAttempts"/> failures the account is locked temporarily.</summary>
    public void RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockout)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
        {
            LockoutUntil = now.Add(lockout);
            FailedLoginCount = 0;
        }
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginCount = 0;
        LockoutUntil = null;
    }

    public void ChangeUsername(string username)
    {
        Username = InputRules.NormalizeUsername(username);
        UsernameNormalized = Username;
        NeedsProfile = false;
    }

    public void ChangeDisplayName(string displayName) => DisplayName = InputRules.Normalize(displayName);

    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

    public void Suspend() => Status = UserStatus.Suspended;

    public void Unsuspend()
    {
        if (Status == UserStatus.Suspended)
        {
            Status = UserStatus.Active;
        }
    }

    public void TouchLastSeen(DateTimeOffset now) => LastSeenAt = now;

    /// <summary>USR-05 / P-15: irreversible soft delete that frees the username and anonymizes PII.</summary>
    public void Delete(DateTimeOffset now)
    {
        var tag = Id.ToString("N");
        Status = UserStatus.Deleted;
        DeletedAt = now;
        Email = $"deleted-{tag}@deleted.invalid";
        EmailNormalized = Email.ToUpperInvariant();
        Username = $"deleted-{tag}";
        UsernameNormalized = Username;
        DisplayName = DeletedDisplayName;
        PasswordHash = null;
        EmailConfirmed = false;
        Raise(new UserDeleted(Id));
    }
}

public sealed record UserRegistered(Guid UserId) : IDomainEvent;

public sealed record UserDeleted(Guid UserId) : IDomainEvent;
