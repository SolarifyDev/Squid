using System.Reflection;

namespace Squid.Api.Extensions;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Build the configuration for the service.
    /// </summary>
    internal static IHostBuilder AddConfiguration(this IHostBuilder host)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        
        host.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddJsonFile(
                path: "appsettings.json",
                optional: false,
                reloadOnChange: true);

            configBuilder.AddJsonFile(
                path: $"appsettings.{environment}.json",
                optional: true,
                reloadOnChange: true);

            configBuilder.AddEnvironmentVariables();

            configBuilder.AddUserSecrets(
                assembly: Assembly.GetExecutingAssembly(),
                optional: true,
                reloadOnChange: true);
        });

        return host;
    }
}