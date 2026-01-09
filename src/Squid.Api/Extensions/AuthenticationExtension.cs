using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Squid.Core.Services.Identity;
using Squid.Core.Settings.Authentication;
using Squid.Core.Settings.System;
using Squid.Message.Constants;
using Squid.Message.Enums.System;

namespace Squid.Api.Extensions;

public static class AuthenticationExtension
{
     public static void AddCustomAuthentication(this IServiceCollection services, IConfiguration configuration)
     {
         services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
             .AddJwtBearer(options =>
             {
                 options.TokenValidationParameters = new TokenValidationParameters
                 {
                     ValidateLifetime = false,
                     ValidateAudience = false,
                     ValidateIssuer = false,
                     ValidateIssuerSigningKey = true,
                     IssuerSigningKey =
                         new SymmetricSecurityKey(
                             Encoding.UTF8.GetBytes(new JwtSymmetricKeySetting(configuration).Value
                                 .PadRight(256 / 8, '\0')))
                 };
             });

        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(
                JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
        });

        RegisterCurrentUser(services, configuration);
    }
     
     private static void RegisterCurrentUser(IServiceCollection services, IConfiguration configuration)
     {
         var appType = new ApiRunModeSetting(configuration).Value;

         switch (appType)
         {
             case ApiRunMode.Api:
                 services.AddScoped<ICurrentUser, ApiUser>();
                 break;
             case ApiRunMode.Internal:
                 services.AddScoped<ICurrentUser, InternalUser>();
                 break;
         }
     }
}