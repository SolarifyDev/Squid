using Squid.Core.Setup;
using Squid.Core.Setup.Embedded;
using Squid.Core.Setup.Install;

namespace Squid.Api.Setup;

/// <summary>
/// Makes sure a self-contained install has finished its first-run setup before the API host is
/// built: unattended when the start parameters already say enough, otherwise through the wizard.
/// </summary>
public static class FirstRunSetup
{
    public static async Task EnsureCompletedAsync(string[] args, IConfiguration configuration, EmbeddedServiceHost embedded)
    {
        if (!SetupRequirement.IsRequired(configuration)) return;

        if (SetupRequirement.IsUnattended(configuration))
        {
            await RunUnattendedAsync(configuration, embedded).ConfigureAwait(false);
            return;
        }

        await SetupHost.RunAsync(args, configuration, embedded).ConfigureAwait(false);
    }

    private static async Task RunUnattendedAsync(IConfiguration configuration, EmbeddedServiceHost embedded)
    {
        var request = UnattendedSetup.BuildRequest(configuration);

        SetupRequestValidator.EnsureValid(request, SetupSession.DetectCapabilities(configuration));

        Log.Information("No database configured but an administrator password was given — setting up with the built-in PostgreSQL");

        await new SetupInstaller(embedded, configuration, new SetupProgressTracker()).InstallAsync(request, CancellationToken.None).ConfigureAwait(false);
    }
}
