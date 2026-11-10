using FluentValidation;
using Messenger.Domain.Common;

namespace Messenger.Application.Common;

/// <summary>FluentValidation adapters for <see cref="InputRules"/> (values are NFC-normalized before checking).</summary>
public static class ValidationRules
{
    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Email is required.")
            .Must(e => InputRules.IsValidEmail(InputRules.Normalize(e))).WithMessage("Email is not valid.");

    public static IRuleBuilderOptions<T, string> ValidPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotNull().WithMessage("Password is required.")
            .Must(p => InputRules.ValidatePassword(p ?? string.Empty) is null)
            .WithMessage((_, p) => InputRules.ValidatePassword(p ?? string.Empty));

    public static IRuleBuilderOptions<T, string> ValidUsername<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotNull().WithMessage("Username is required.")
            .Must(u => InputRules.ValidateUsername(InputRules.NormalizeUsername(u ?? string.Empty)) is null)
            .WithMessage((_, u) => InputRules.ValidateUsername(InputRules.NormalizeUsername(u ?? string.Empty)));

    public static IRuleBuilderOptions<T, string> ValidDisplayName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotNull().WithMessage("Display name is required.")
            .Must(d => InputRules.ValidateDisplayName(InputRules.Normalize(d)) is null)
            .WithMessage((_, d) => InputRules.ValidateDisplayName(InputRules.Normalize(d)));

    public static IRuleBuilderOptions<T, string> ValidMessageBody<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotNull().WithMessage("Message must not be empty.")
            .Must(b => InputRules.ValidateMessageBody(InputRules.Normalize(b)) is null)
            .WithMessage((_, b) => InputRules.ValidateMessageBody(InputRules.Normalize(b)));

    public static IRuleBuilderOptions<T, string> ValidGroupTitle<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotNull().WithMessage("Group title is required.")
            .Must(t => InputRules.ValidateGroupTitle(InputRules.Normalize(t)) is null)
            .WithMessage((_, t) => InputRules.ValidateGroupTitle(InputRules.Normalize(t)));

    public static IRuleBuilderOptions<T, string> ValidOpaqueToken<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Token is required.").MaximumLength(200);
}
