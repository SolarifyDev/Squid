using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Squid.Core.Services.Authentication;

public interface IAgentTokenService : IScopedDependency
{
    string GenerateToken(string purpose, TimeSpan expiry);
    bool ValidateToken(string token);
}

public class AgentTokenService : IAgentTokenService
{
    private readonly byte[] _keyBytes;

    public AgentTokenService(IConfiguration configuration)
    {
        var key = configuration.GetValue<string>("Authentication:Jwt:SymmetricKey");

        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Authentication:Jwt:SymmetricKey is not configured");

        _keyBytes = Encoding.UTF8.GetBytes(key);
    }

    public string GenerateToken(string purpose, TimeSpan expiry)
    {
        var securityKey = new SymmetricSecurityKey(_keyBytes);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("purpose", purpose),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: "squid",
            audience: "squid-agent",
            claims: claims,
            expires: DateTime.UtcNow.Add(expiry),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool ValidateToken(string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var securityKey = new SymmetricSecurityKey(_keyBytes);

        try
        {
            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "squid",
                ValidateAudience = true,
                ValidAudience = "squid-agent",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
