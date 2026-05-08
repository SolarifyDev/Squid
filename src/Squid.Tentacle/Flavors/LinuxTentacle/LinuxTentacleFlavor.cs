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

        var registrar = ResolveRegistrar(communicationMode, tentacleSettings, context.ForceRegistration);

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
    /// Determines whether to register on this startup or skip.
    ///
    /// The flag <c>Tentacle:Registered=true</c> is set by the <c>register</c>
    /// command after a successful registration and persisted to the instance
    /// config file. This is the **only** reliable indicator that the Server
    /// already knows about this Tentacle.
    ///
    /// We can NOT use <c>ServerCertificate != empty</c> alone because Docker
    /// users legitimately pass <c>Tentacle__ServerCertificate</c> for TLS
    /// pinning on first run — before the machine has been registered. Using
    /// that field as the "already registered" marker would silently skip
    /// registration, leaving the Server unaware of the Tentacle and all
    /// poll connections rejected.
    ///
    /// <para><paramref name="forceRegistration"/> bypasses the skip path —
    /// set by <c>RegisterCommand</c> when the operator passes
    /// <c>--force</c>. Catches the operator-impact gap where re-registering
    /// to update roles / environment / api-key was silently no-op'd
    /// (caught by Linux C3h E2E first runner; documented in the spawned
    /// production-fix task).</para>
    /// </summary>
    private static ITentacleRegistrar ResolveRegistrar(
        TentacleCommunicationMode mode, TentacleSettings settings, bool forceRegistration)
    {
        var alreadyRegistered = settings.Registered.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (alreadyRegistered && !forceRegistration)
        {
            Log.Information("Tentacle already registered (Registered=true), skipping re-registration. Pass --force to re-register against the server (e.g. to update roles/environment/api-key).");
            return new NoOpRegistrar(settings);
        }

        if (alreadyRegistered && forceRegistration)
            Log.Information("--force passed: bypassing 'already registered' skip and re-registering");

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
