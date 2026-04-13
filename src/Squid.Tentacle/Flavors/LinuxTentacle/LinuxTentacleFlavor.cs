using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Flavors.LinuxTentacle.Configuration;
using Squid.Tentacle.ScriptExecution;
using Serilog;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

public sealed class LinuxTentacleFlavor : ITentacleFlavor
{
    public string Id => "LinuxTentacle";

    public TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context)
    {
        var tentacleSettings = context.TentacleSettings;

        var settings = new LinuxTentacleSettings();
        context.Configuration.GetSection("LinuxTentacle").Bind(settings);

        var communicationMode = ResolveCommunicationMode(tentacleSettings);

        Log.Information("LinuxTentacle starting in {Mode} mode", communicationMode);

        var registrar = communicationMode == TentacleCommunicationMode.Polling
            ? (ITentacleRegistrar)new LinuxTentacleRegistrar(tentacleSettings)
            : new LinuxListeningRegistrar(tentacleSettings);

        var backend = new LocalScriptService();

        return new TentacleFlavorRuntime
        {
            Registrar = registrar,
            ScriptBackend = backend,
            CommunicationMode = communicationMode,
            ListeningPort = settings.ListeningPort,
            BackgroundTasks = [],
            StartupHooks = [],
            ReadinessCheck = null,
            Metadata = new Dictionary<string, string>
            {
                ["flavor"] = Id,
                ["os"] = Environment.OSVersion.ToString(),
                ["communicationMode"] = communicationMode.ToString(),
                ["workspacePath"] = settings.WorkspacePath
            }
        };
    }

    private static TentacleCommunicationMode ResolveCommunicationMode(TentacleSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.ServerCommsUrl)
            && string.IsNullOrWhiteSpace(settings.ServerCommsAddresses)
            ? TentacleCommunicationMode.Listening
            : TentacleCommunicationMode.Polling;
    }
}
