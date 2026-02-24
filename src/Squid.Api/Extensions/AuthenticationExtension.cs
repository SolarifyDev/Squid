using System.Text;
using Microsoft.IdentityModel.Tokens;
using Squid.Message.Constants;

namespace Squid.Api.Extensions;

public static class AuthenticationExtension
{
    public static void AddCustomAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtKey = configuration.GetValue<string>("Authentication:Jwt:SymmetricKey");

        if (string.IsNullOrEmpty(jwtKey)) return;

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AuthenticationSchemeConstants.UserJwtAuthenticationScheme;
                options.DefaultChallengeScheme = AuthenticationSchemeConstants.UserJwtAuthenticationScheme;
            })
            .AddJwtBearer(AuthenticationSchemeConstants.AgentJwtAuthenticationScheme, options =>
            {
                options.TokenValidationParameters = BuildTokenValidationParameters(jwtKey, "squid-agent");
            })
            .AddJwtBearer(AuthenticationSchemeConstants.UserJwtAuthenticationScheme, options =>
            {
                options.TokenValidationParameters = BuildTokenValidationParameters(jwtKey, "squid-api");
            });

        services.AddAuthorization();
    }

    private static TokenValidationParameters BuildTokenValidationParameters(string jwtKey, string audience)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "squid",
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    }
}
