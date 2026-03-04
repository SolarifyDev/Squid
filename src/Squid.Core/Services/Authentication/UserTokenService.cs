using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Settings.Authentication;

namespace Squid.Core.Services.Authentication;

public interface IUserTokenService : IScopedDependency
{
    (string AccessToken, DateTime ExpiresAtUtc) GenerateToken(UserAccount userAccount);
}

public class UserTokenService : IUserTokenService
{
    private readonly byte[] _keyBytes;
    private readonly double _expiryHours;

    public UserTokenService(IConfiguration configuration)
    {
        var key = new JwtSymmetricKeySetting(configuration).Value;

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Authentication:Jwt:SymmetricKey is not configured");

        _keyBytes = Encoding.UTF8.GetBytes(key.PadRight(256 / 8, '\0'));
        _expiryHours = configuration.GetValue<double?>("Authentication:Jwt:UserTokenExpiryHours") ?? 24;
    }

    public (string AccessToken, DateTime ExpiresAtUtc) GenerateToken(UserAccount userAccount)
    {
        var securityKey = new SymmetricSecurityKey(_keyBytes);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var expiresAtUtc = DateTime.UtcNow.AddHours(_expiryHours <= 0 ? 24 : _expiryHours);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userAccount.Id.ToString()),
            new(ClaimTypes.Name, userAccount.UserName),
            new("display_name", userAccount.DisplayName ?? userAccount.UserName),
            new("is_system", userAccount.IsSystem.ToString().ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: "squid",
            audience: "squid-api",
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
