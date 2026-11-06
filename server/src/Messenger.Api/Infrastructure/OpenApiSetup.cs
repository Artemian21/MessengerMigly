using System.Text.Json.Nodes;
using Messenger.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Messenger.Api.Infrastructure;

/// <summary>API-04: OpenAPI document with Bearer scheme, descriptions and realistic request examples (rendered by Scalar at /docs).</summary>
public static class OpenApiSetup
{
    private static readonly Dictionary<Type, string> Examples = new()
    {
        [typeof(RegisterRequest)] = """{"email":"alice@example.com","password":"S3cure-pass-123","username":"alice","displayName":"Alice Kovalenko"}""",
        [typeof(LoginRequest)] = """{"email":"alice@example.com","password":"S3cure-pass-123"}""",
        [typeof(EmailRequest)] = """{"email":"alice@example.com"}""",
        [typeof(TokenRequest)] = """{"token":"q2V9mZ0bX3...from-the-email-link"}""",
        [typeof(ResetPasswordRequest)] = """{"token":"q2V9mZ0bX3...from-the-email-link","newPassword":"An0ther-strong-pass"}""",
        [typeof(ChangePasswordRequest)] = """{"currentPassword":"S3cure-pass-123","newPassword":"An0ther-strong-pass"}""",
        [typeof(CompleteProfileRequest)] = """{"username":"alice.k","displayName":"Alice K."}""",
        [typeof(UpdateProfileRequest)] = """{"displayName":"Alice from room 412"}""",
        [typeof(ChangeUsernameRequest)] = """{"username":"alice_k"}""",
        [typeof(DeleteAccountRequest)] = """{"password":"S3cure-pass-123"}""",
        [typeof(SendMessageRequest)] = """{"clientMessageId":"0192f0c4-7d4e-7a3b-9f10-3c2a1b0e9d11","body":"Hey, are you ready for class?","replyToMessageId":null,"attachments":[]}""",
        [typeof(EditMessageRequest)] = """{"body":"Hey, are you ready for the lab?"}""",
        [typeof(CreateGroupRequest)] = """{"title":"Room 412 · Study crew","memberIds":["0192f0c4-7d4e-7a3b-9f10-3c2a1b0e9d12","0192f0c4-7d4e-7a3b-9f10-3c2a1b0e9d13"]}""",
        [typeof(RenameGroupRequest)] = """{"title":"Room 412 · Exam season"}""",
        [typeof(AddMembersRequest)] = """{"userIds":["0192f0c4-7d4e-7a3b-9f10-3c2a1b0e9d14"]}""",
        [typeof(ChangeRoleRequest)] = """{"role":"Admin"}""",
        [typeof(MarkReadRequest)] = """{"upToSequence":42}""",
        [typeof(AckDeliveredRequest)] = """{"messageIds":["0192f0c4-7d4e-7a3b-9f10-3c2a1b0e9d20"]}""",
        [typeof(PushSubscriptionRequest)] = """{"endpoint":"https://fcm.googleapis.com/fcm/send/abc123","keys":{"p256dh":"BNcR...","auth":"tBHI..."}}""",
        [typeof(UnsubscribePushRequest)] = """{"endpoint":"https://fcm.googleapis.com/fcm/send/abc123"}""",
    };

    public static IServiceCollection AddMessengerOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Migly Messenger API",
                    Version = "v1",
                    Description = """
                        REST API of the Migly real-time messenger (Lab 01 + Lab 21).
                        Commands go over REST; live events are pushed over SignalR at `/hubs/chat`.
                        Authenticate with `POST /api/v1/auth/login`, then send `Authorization: Bearer <accessToken>`.
                        The refresh token is an HttpOnly cookie; `/auth/refresh` also requires the header `X-Requested-With: XMLHttpRequest`.
                        Errors use RFC 9457 problem details; validation errors return 422 with an `errors` map.
                        """,
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token from /api/v1/auth/login or /api/v1/auth/refresh.",
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var requiresAuth = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
                if (requiresAuth)
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
                        },
                    ];
                    operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or expired access token." });
                }

                operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Rate limit exceeded (see Retry-After)." });
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (Examples.TryGetValue(context.JsonTypeInfo.Type, out var json))
                {
                    schema.Example = ToOpenApi(JsonNode.Parse(json));
                }

                return Task.CompletedTask;
            });
        });
        return services;
    }

    private static IOpenApiAny ToOpenApi(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Aggregate(new OpenApiObject(), (acc, kv) => { acc[kv.Key] = ToOpenApi(kv.Value); return acc; }),
        JsonArray arr => arr.Aggregate(new OpenApiArray(), (acc, item) => { acc.Add(ToOpenApi(item)); return acc; }),
        JsonValue value when value.TryGetValue<bool>(out var b) => new OpenApiBoolean(b),
        JsonValue value when value.TryGetValue<long>(out var l) => new OpenApiLong(l),
        JsonValue value when value.TryGetValue<string>(out var s) => new OpenApiString(s),
        _ => new OpenApiNull(),
    };
}
