using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aiq.Gateway.Auth;

public interface IJwtTokenService
{
    AccessToken Issue(UserRecord user, IReadOnlyCollection<string> roles, Guid? tenantId = null);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public AccessToken Issue(UserRecord user, IReadOnlyCollection<string> roles, Guid? tenantId = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new("sub",   user.Id.ToString()),
            new("email", user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        if (tenantId is { } tid)
        {
            claims.Add(new Claim("tenant_id", tid.ToString()));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var serialized = new JwtSecurityTokenHandler().WriteToken(token);
        return new AccessToken(serialized, expiresAt);
    }
}
