using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Users;

namespace Messenger.Application.Push;

public sealed record PushConfigDto(bool Enabled, string? PublicKey);

public sealed record GetPushConfigQuery : IQuery<Result<PushConfigDto>>, IAllowAnonymous;

public sealed class GetPushConfigHandler(IPushSender sender) : IRequestHandler<GetPushConfigQuery, Result<PushConfigDto>>
{
    public Task<Result<PushConfigDto>> Handle(GetPushConfigQuery request, CancellationToken ct) =>
        Task.FromResult(Result.Success(new PushConfigDto(sender.IsEnabled, sender.IsEnabled ? sender.PublicKey : null)));
}

// NTF-01
public sealed record SubscribePushCommand(string Endpoint, string P256dh, string Auth) : ICommand<Result>, IRetryOnUniqueViolation;

public sealed class SubscribePushValidator : AbstractValidator<SubscribePushCommand>
{
    public SubscribePushValidator()
    {
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(1024)
            .Must(e => !InputRules.HasForbiddenControlChars(e, allowLineBreaks: false)
                       && Uri.TryCreate(e, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .WithMessage("Endpoint must be an absolute https URL.");
        RuleFor(x => x.P256dh).NotEmpty().MaximumLength(256)
            .Must(v => !InputRules.HasForbiddenControlChars(v, allowLineBreaks: false)).WithMessage("Endpoint must be an absolute https URL.");
        RuleFor(x => x.Auth).NotEmpty().MaximumLength(128)
            .Must(v => !InputRules.HasForbiddenControlChars(v, allowLineBreaks: false)).WithMessage("Endpoint must be an absolute https URL.");
    }
}

public sealed class SubscribePushHandler(IPushSubscriptionRepository repository, IPushSender sender, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<SubscribePushCommand, Result>
{
    public async Task<Result> Handle(SubscribePushCommand request, CancellationToken ct)
    {
        if (!sender.IsEnabled)
        {
            return Error.NotFound("Push notifications");
        }

        var existing = await repository.FindByEndpointAsync(request.Endpoint, ct);
        if (existing is not null)
        {
            existing.Update(currentUser.RequiredUserId, request.P256dh, request.Auth);
        }
        else
        {
            repository.Add(PushSubscription.Create(currentUser.RequiredUserId, request.Endpoint, request.P256dh, request.Auth, clock.UtcNow));
        }

        return Result.Success();
    }
}

public sealed record UnsubscribePushCommand(string Endpoint) : ICommand<Result>;

public sealed class UnsubscribePushValidator : AbstractValidator<UnsubscribePushCommand>
{
    public UnsubscribePushValidator() =>
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(1024)
            .Must(e => !InputRules.HasForbiddenControlChars(e, allowLineBreaks: false)).WithMessage("Endpoint must be an absolute https URL.");
}

public sealed class UnsubscribePushHandler(IPushSubscriptionRepository repository, ICurrentUser currentUser) : IRequestHandler<UnsubscribePushCommand, Result>
{
    public async Task<Result> Handle(UnsubscribePushCommand request, CancellationToken ct)
    {
        var existing = await repository.FindByEndpointAsync(request.Endpoint ?? string.Empty, ct);
        if (existing is not null && existing.UserId == currentUser.RequiredUserId)
        {
            repository.Remove(existing);
        }

        return Result.Success();
    }
}
