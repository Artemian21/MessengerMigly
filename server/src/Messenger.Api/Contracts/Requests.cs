using Messenger.Application.Common;
using Messenger.Application.Messages;

namespace Messenger.Api.Contracts;

public sealed record RegisterRequest(string Email, string Password, string Username, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record EmailRequest(string Email);

public sealed record TokenRequest(string Token);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string NewPassword);

public sealed record CompleteProfileRequest(string Username, string? DisplayName);

public sealed record UpdateProfileRequest(string DisplayName);

public sealed record ChangeUsernameRequest(string Username);

public sealed record DeleteAccountRequest(string? Password);

public sealed record SendMessageRequest(Guid ClientMessageId, string Body, Guid? ReplyToMessageId, IReadOnlyList<AttachmentInput>? Attachments);

public sealed record EditMessageRequest(string Body);

public sealed record CreateGroupRequest(string Title, IReadOnlyList<Guid> MemberIds);

public sealed record RenameGroupRequest(string Title);

public sealed record AddMembersRequest(IReadOnlyList<Guid> UserIds);

public sealed record ChangeRoleRequest(string Role);

public sealed record MarkReadRequest(long UpToSequence);

public sealed record AckDeliveredRequest(IReadOnlyList<Guid> MessageIds);

public sealed record PushKeys(string P256dh, string Auth);

public sealed record PushSubscriptionRequest(string Endpoint, PushKeys Keys);

public sealed record UnsubscribePushRequest(string Endpoint);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, MeDto User);

public sealed record AcceptedResponse(string Message);

public sealed record MarkReadResponse(long LastReadSequence);

public sealed record AckDeliveredResponse(int Acknowledged);

public sealed record HealthResponse(string Status);

public sealed record ProvidersResponse(IReadOnlyList<string> Providers);
