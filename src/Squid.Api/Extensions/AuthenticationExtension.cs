using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Squid.Api.Authentication.ApiKey;
using Squid.Core.Settings.Authentication;
using Squid.Message.Constants;

namespace Squid.Api.Extensions;

public static class AuthenticationExtension
{
    public static void AddCustomAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtKey = new JwtSymmetricKeySetting(configuration).Value;

        if (string.IsNullOrEmpty(jwtKey)) return;

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateLifetime = false,
                    ValidateAudience = false,
                    ValidateIssuer = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtKey.PadRight(256 / 8, '\0')))
                };
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                AuthenticationSchemeConstants.ApiKeyAuthenticationScheme, _ => { });

        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(
                JwtBearerDefaults.AuthenticationScheme,
                AuthenticationSchemeConstants.ApiKeyAuthenticationScheme).RequireAuthenticatedUser().Build();
        });
    }
}
