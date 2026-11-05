using Messenger.Domain.Common;
using Messenger.Domain.Users;
using Shouldly;

namespace Messenger.Domain.Tests;

public sealed class UserAndRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("", "Message must not be empty.")]
    [InlineData("   ", "Message must not be empty.")]
    [InlineData("hi\u0000there", "Message contains forbidden control characters.")]
    [InlineData("evil‮text", "Message contains forbidden control characters.")]
    public void Invalid_message_bodies_are_rejected(string body, string expected)
    {
        InputRules.ValidateMessageBody(InputRules.Normalize(body)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData("a\u0000b@example.com", false)]
    [InlineData("a\tb@example.com", false)]
    [InlineData("a b@example.com", false)]
    [InlineData("no-at-sign.example.com", false)]
    public void Email_must_be_well_formed_and_free_of_control_characters(string email, bool valid)
    {
        InputRules.IsValidEmail(email).ShouldBe(valid);
    }

    [Fact]
    public void Message_length_counts_user_perceived_characters()
    {
        InputRules.ValidateMessageBody(new string('a', 4000)).ShouldBeNull();
        InputRules.ValidateMessageBody(new string('a', 4001)).ShouldNotBeNull();
        InputRules.ValidateMessageBody(string.Concat(Enumerable.Repeat("👍", 4000))).ShouldBeNull();
    }

    [Fact]
    public void Line_breaks_and_emoji_sequences_are_allowed()
    {
        InputRules.ValidateMessageBody("line one\nline two 👨‍👩‍👧").ShouldBeNull();
    }

    [Fact]
    public void Text_is_normalized_to_nfc_and_trimmed()
    {
        InputRules.Normalize("  Café  ").ShouldBe("Café");
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("al", false)]
    [InlineData("alice.k_2", true)]
    [InlineData("Alice", false)]
    [InlineData("al ice", false)]
    public void Username_format(string username, bool valid)
    {
        InputRules.IsValidUsername(username).ShouldBe(valid);
    }

    [Fact]
    public void Password_policy_enforces_length_and_bcrypt_byte_limit()
    {
        InputRules.ValidatePassword("short").ShouldNotBeNull();
        InputRules.ValidatePassword("S3cure-pass-123").ShouldBeNull();
        InputRules.ValidatePassword(new string('я', 40)).ShouldNotBeNull();
    }

    [Fact]
    public void Lockout_after_repeated_failures()
    {
        var user = User.Register("a@example.com", "alice", "Alice", "hash", Now);
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, 5, TimeSpan.FromMinutes(15));
        }

        user.IsLockedOut(Now.AddMinutes(1)).ShouldBeTrue();
        user.IsLockedOut(Now.AddMinutes(16)).ShouldBeFalse();
    }

    [Fact]
    public void Deleting_user_anonymizes_and_frees_username()
    {
        var user = User.Register("a@example.com", "alice", "Alice", "hash", Now);

        user.Delete(Now);

        user.Status.ShouldBe(UserStatus.Deleted);
        user.Email.ShouldNotContain("example.com");
        user.UsernameNormalized.ShouldNotBe("alice");
        user.DisplayName.ShouldBe(User.DeletedDisplayName);
        user.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public void Rotated_refresh_token_is_accepted_only_within_grace()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(21), null, null);
        var next = RefreshToken.Create(token.UserId, token.FamilyId, "hash2", Now, TimeSpan.FromDays(21), null, null);

        token.ReplaceWith(next, Now);

        token.IsActive(Now).ShouldBeFalse();
        token.IsWithinRotationGrace(Now.AddSeconds(10), TimeSpan.FromSeconds(20)).ShouldBeTrue();
        token.IsWithinRotationGrace(Now.AddSeconds(30), TimeSpan.FromSeconds(20)).ShouldBeFalse();
    }

    [Fact]
    public void User_token_is_single_use_and_expires()
    {
        var token = UserToken.Create(Guid.NewGuid(), UserTokenType.EmailVerification, "hash", Now, TimeSpan.FromHours(24));

        token.Use(Now.AddHours(25)).Error!.Code.ShouldBe("token_expired");
        token.Use(Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        token.Use(Now.AddHours(2)).Error!.Code.ShouldBe("token_used");
    }
}
