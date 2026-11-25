using Squid.Core.Services.Packages.Storage;
using Squid.Core.Setup;
using Squid.Core.Setup.Embedded;
using Squid.Core.Setup.Install;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.Server;
using Squid.Core.Settings.Setup;
using Squid.Message.Models.Setup;

namespace Squid.Api.Setup;

/// <summary>State shared by the setup endpoints for the lifetime of one setup host.</summary>
public sealed class SetupSession
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SetupSession(IConfiguration configuration, EmbeddedServiceHost embedded, SetupToken token)
    {
        Configuration = configuration;
        Embedded = embedded;
        Token = token;
        Capabilities = DetectCapabilities(configuration);
        PackageStoreProviders = PackageStoreProviderCatalog.Create(configuration);
    }

    public IConfiguration Configuration { get; }
    public EmbeddedServiceHost Embedded { get; }
    public SetupToken Token { get; }
    public SetupCapabilities Capabilities { get; }
    public SetupProgressTracker Tracker { get; } = new();

    /// <summary>Built from the same configuration (so the same data directory) the installed server reads, with no container or database.</summary>
    public IReadOnlyList<IPackageBlobStoreProvider> PackageStoreProviders { get; }

    public Task Completed => _completed.Task;

    public void MarkCompleted() => _completed.TrySetResult();

    public SetupStatus BuildStatus() => new()
    {
        Required = true,
        BuiltinDatabaseAvailable = Capabilities.BuiltinDatabaseAvailable,
        BuiltinRedisAvailable = Capabilities.BuiltinRedisAvailable,
        SuggestedExternalUrl = new ServerUrlSetting(Configuration).ExternalUrl,
        DefaultPollingPort = new HalibutSetting(Configuration).Polling.Port,
        DataVolumeWarning = DataVolumeInspector.FindAnonymousVolumeWarning(new DataDirectorySetting(Configuration).Value)
    };

    public static SetupCapabilities DetectCapabilities(IConfiguration configuration)
    {
        var embedded = new EmbeddedServicesSetting(configuration);

        return new SetupCapabilities(EmbeddedServiceHost.IsPostgresAvailable(embedded.Postgres), EmbeddedServiceHost.IsRedisAvailable(embedded.Redis));
    }
}
