using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Messenger.Application.Abstractions;
using Messenger.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Messenger.Infrastructure.Security;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class PasswordHashingOptions
{
    public int WorkFactor { get; set; } = 11;
}

/// <summary>AUTH-02: BCrypt enhanced hashing (SHA-384 pre-hash), unique salt per password.</summary>
public sealed class BCryptPasswordHasher(IOptions<PasswordHashingOptions> options) : IPasswordHasher
{
    public string Hash(string password) =>
        BCrypt.Net.BCrypt.EnhancedHashPassword(password, Math.Clamp(options.Value.WorkFactor, 4, 16));

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.EnhancedVerify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}

public sealed class JwtOptions
{
    public const string DevelopmentSecret = "development-only-secret-change-me-0123456789abcdef";

    public string Secret { get; set; } = string.Empty;
    public int AccessTtlMinutes { get; set; } = 12;

    public SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(Secret));
}

/// <summary>AUTH-13: HS256 access token with only sub, sid, role, jti and exp.</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, Guid sessionId, SystemRole role)
    {
        var expires = clock.UtcNow.AddMinutes(Math.Clamp(options.Value.AccessTtlMinutes, 1, 60));
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Sid] = sessionId.ToString(),
                [ClaimTypesShort.Role] = role.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [JwtRegisteredClaimNames.Exp] = expires.ToUnixTimeSeconds(),
            },
            SigningCredentials = new SigningCredentials(options.Value.SigningKey(), SecurityAlgorithms.HmacSha256),
        };
        return (_handler.CreateToken(descriptor), expires);
    }

    public string GenerateOpaqueToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class ClaimTypesShort
{
    public const string Role = "role";
    public const string Sub = JwtRegisteredClaimNames.Sub;
    public const string Sid = JwtRegisteredClaimNames.Sid;

    public static Guid? GetGuid(this ClaimsPrincipal principal, string claim) =>
        Guid.TryParse(principal.FindFirst(claim)?.Value, out var id) ? id : null;
}
