using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Flavors.Tentacle.Configuration;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.SelfHeal;
using Serilog;

namespace Squid.Tentacle.Flavors.Tentacle;

public sealed class TentacleFlavor : ITentacleFlavor
{
    public string Id => "Tentacle";

    // "LinuxTentacle" was the original id — kept as an alias so already-deployed agents
    // (Linux AND Windows: this flavor is cross-platform) and old install snippets that pass
    // --flavor LinuxTentacle keep resolving. New snippets emit --flavor Tentacle.
    public IReadOnlyCollection<string> Aliases => new[] { "LinuxTentacle" };

    public TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context)
    {
        var tentacleSettings = context.TentacleSettings;

        var settings = new TentacleFlavorSettings();
        // Read the legacy "LinuxTentacle" section first, then the neutral "TentacleFlavor"
        // section (a distinct section name so it never collides with the general "Tentacle"
        // settings). If both are present, the neutral section wins.
        context.Configuration.GetSection("LinuxTentacle").Bind(settings);
        context.Configuration.GetSection("TentacleFlavor").Bind(settings);

        var communicationMode = ResolveCommunicationMode(tentacleSettings);

        Log.Information("Tentacle ({Os}) starting in {Mode} mode", System.Environment.OSVersion.Platform, communicationMode);

        var registrar = ResolveRegistrar(communicationMode, tentacleSettings, context.ForceRegistration);

        var backend = new LocalScriptService();

        return new TentacleFlavorRuntime
        {
            Registrar = registrar,
            ScriptBackend = backend,
            CommunicationMode = communicationMode,
            ListeningPort = settings.ListeningPort,
            // Disk-pressure self-heal: reclaim completed-script workspaces when the
            // temp disk runs low, so a flood of deployments can't fill the disk and
            // turn every subsequent deploy into a disk-full failure. The backend is
            // the running-script reporter, so an in-flight deployment's workspace is
            // never swept.
            BackgroundTasks = [SelfHealBackgroundTask.ForLocalWorkspaces(backend)],
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
                "'squid-tentacle register --server URL --api-key KEY --flavor Tentacle' first");
            return new NoOpRegistrar(settings);
        }

        return mode == TentacleCommunicationMode.Polling
            ? new TentaclePollingRegistrar(settings)
            : new TentacleListeningRegistrar(settings);
    }
}
