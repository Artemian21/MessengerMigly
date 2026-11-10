using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Messenger.Domain.Common;

/// <summary>Shared text rules (MSG-02, USR-02). All user text is NFC-normalized before validation.</summary>
public static partial class InputRules
{
    public const int MessageMaxLength = 4000;
    public const int DisplayNameMaxLength = 64;
    public const int GroupTitleMaxLength = 100;
    public const int PasswordMinLength = 10;
    public const int PasswordMaxBytes = 72;
    public const int EmailMaxLength = 254;

    public static string Normalize(string? value) => (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();

    public static string NormalizeEmail(string email) => Normalize(email).ToUpperInvariant();

    public static string NormalizeUsername(string username) => Normalize(username).ToLowerInvariant();

    /// <summary>Control characters are rejected, except line breaks and tabs inside message bodies.</summary>
    public static bool HasForbiddenControlChars(string value, bool allowLineBreaks)
    {
        foreach (var ch in value)
        {
            if (allowLineBreaks && (ch == '\n' || ch == '\r' || ch == '\t'))
            {
                continue;
            }

            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format && ch != '‍')
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsValidUsername(string normalized) => UsernameRegex().IsMatch(normalized);

    public static bool IsValidEmail(string email) =>
        email.Length <= EmailMaxLength && !HasForbiddenControlChars(email, allowLineBreaks: false) && EmailRegex().IsMatch(email);

    /// <summary>Text length in user-perceived characters (grapheme clusters), so emoji count once.</summary>
    public static int TextLength(string value) => new StringInfo(value).LengthInTextElements;

    public static string? ValidateMessageBody(string normalized)
    {
        if (normalized.Length == 0)
        {
            return "Message must not be empty.";
        }

        if (TextLength(normalized) > MessageMaxLength)
        {
            return $"Message must be at most {MessageMaxLength} characters.";
        }

        return HasForbiddenControlChars(normalized, allowLineBreaks: true)
            ? "Message contains forbidden control characters."
            : null;
    }

    public static string? ValidateDisplayName(string normalized)
    {
        if (normalized.Length == 0 || TextLength(normalized) > DisplayNameMaxLength)
        {
            return $"Display name must be 1-{DisplayNameMaxLength} characters.";
        }

        return HasForbiddenControlChars(normalized, allowLineBreaks: false)
            ? "Display name contains forbidden control characters."
            : null;
    }

    public static string? ValidateUsername(string normalized) =>
        IsValidUsername(normalized) ? null : "Username must be 3-32 characters: a-z, 0-9, '_' or '.'.";

    public static string? ValidatePassword(string password)
    {
        if (password.Length < PasswordMinLength)
        {
            return $"Password must be at least {PasswordMinLength} characters.";
        }

        return Encoding.UTF8.GetByteCount(password) > PasswordMaxBytes
            ? $"Password must be at most {PasswordMaxBytes} bytes."
            : null;
    }

    public static string? ValidateGroupTitle(string normalized)
    {
        if (normalized.Length == 0 || TextLength(normalized) > GroupTitleMaxLength)
        {
            return $"Group title must be 1-{GroupTitleMaxLength} characters.";
        }

        return HasForbiddenControlChars(normalized, allowLineBreaks: false)
            ? "Group title contains forbidden control characters."
            : null;
    }

    [GeneratedRegex("^[a-z0-9_.]{3,32}$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
