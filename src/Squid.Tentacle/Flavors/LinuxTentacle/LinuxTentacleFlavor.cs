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

        var registrar = ResolveRegistrar(communicationMode, tentacleSettings);

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

    /// <summary>
    /// If the tentacle has already been registered (ServerCertificate is set),
    /// skip re-registration on startup — just like Listening mode does.
    /// This avoids requiring an API key on every restart.
    /// </summary>
    private static ITentacleRegistrar ResolveRegistrar(
        TentacleCommunicationMode mode, TentacleSettings settings)
    {
        var alreadyRegistered = !string.IsNullOrWhiteSpace(settings.ServerCertificate);

        if (alreadyRegistered)
        {
            Log.Information("Tentacle already registered (ServerCertificate present), skipping re-registration");
            return new NoOpRegistrar(settings);
        }

        // Listening mode without credentials → can't self-register; this tentacle must
        // either be pre-registered via the UI or the operator needs to run `register`
        // first. Return NoOp + warn clearly instead of letting TentacleListeningRegistrar
        // fail deep in the HTTP client with a mysterious 401.
        if (mode == TentacleCommunicationMode.Listening
            && string.IsNullOrWhiteSpace(settings.ApiKey)
            && string.IsNullOrWhiteSpace(settings.BearerToken))
        {
            Log.Warning("Listening mode without credentials — machine must be added via the UI or by running " +
                "'squid-tentacle register --server URL --api-key KEY --flavor LinuxTentacle' first");
            return new NoOpRegistrar(settings);
        }

        return mode == TentacleCommunicationMode.Polling
            ? new TentaclePollingRegistrar(settings)
            : new TentacleListeningRegistrar(settings);
    }
}
