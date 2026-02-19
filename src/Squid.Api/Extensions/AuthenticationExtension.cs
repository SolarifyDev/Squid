using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Squid.Api.Extensions;

public static class AuthenticationExtension
{
    public static void AddCustomAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtKey = configuration.GetValue<string>("Authentication:Jwt:SymmetricKey");

        if (string.IsNullOrEmpty(jwtKey)) return;

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "squid",
                    ValidateAudience = true,
                    ValidAudience = "squid-agent",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        services.AddAuthorization();
    }
}
