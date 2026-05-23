using Autofac;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// H2 — hydrates the <see cref="InMemoryMachineRuntimeCapabilitiesCache"/>
/// from the <c>machine.runtime_capabilities_json</c> column at server
/// startup, so a freshly-deployed pod doesn't make every operator action
/// hit the H1 NoOsDetected path until the next scheduled health check.
///
/// <para>Autofac calls <see cref="Start"/> once at container build time on
/// every <see cref="IStartable"/> singleton. We block on the async load
/// inline — startup is single-threaded and there's no benefit to deferring
/// the cache warm-up.</para>
///
/// <para><b>Failure mode</b>: if the DB read fails (server can't reach
/// Postgres, etc.), we log + continue with an empty cache. Operators will
/// see the H1 NoOsDetected message on the first upgrade-info call until
/// the next health check populates the cache — strictly better than
/// blocking server startup on a transient DB issue.</para>
/// </summary>
public sealed class MachineRuntimeCapabilitiesCacheHydrator : IStartable
{
    private readonly IComponentContext _container;

    /// <summary>
    /// Take the container itself (not the persistence service or the cache
    /// directly) because <see cref="IMachineRuntimeCapabilitiesPersistence"/>
    /// is scoped (<see cref="IScopedDependency"/>) — we need to open a
    /// life-time scope for the one-off startup query, then dispose it. The
    /// cache itself is a singleton so we resolve it from the root context.
    /// </summary>
    public MachineRuntimeCapabilitiesCacheHydrator(IComponentContext container)
    {
        _container = container;
    }

    public void Start()
    {
        try
        {
            HydrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Don't block server startup on a transient DB read failure.
            // Operators will fall through to the H1 NoOsDetected path on
            // the first upgrade-info call, which already gives them an
            // actionable next step (run a health check).
            Log.Warning(ex,
                "MachineRuntimeCapabilitiesCacheHydrator failed to load persisted runtime capabilities — " +
                "server will start with an empty in-memory cache and rely on the next health check per machine. " +
                "Operators may see NoOsDetected on first upgrade-info call until that happens.");
        }
    }

    private async Task HydrateAsync(CancellationToken ct)
    {
        var cache = _container.Resolve<IMachineRuntimeCapabilitiesCache>();

        // Persistence is scoped (per the IScopedDependency registration) so
        // we MUST open a lifetime scope to resolve it. ILifetimeScope-aware
        // services like the EF DbContext need a unit-of-work boundary.
        // Autofac doesn't expose BeginLifetimeScopeAsync (only the sync
        // BeginLifetimeScope()); DisposeAsync via `await using` still works
        // because ILifetimeScope implements IAsyncDisposable.
        await using var scope = _container.Resolve<ILifetimeScope>().BeginLifetimeScope();

        var persistence = scope.Resolve<IMachineRuntimeCapabilitiesPersistence>();

        var rows = await persistence.LoadAllAsync(ct).ConfigureAwait(false);

        var hydrated = 0;
        foreach (var (machineId, capabilities) in rows)
        {
            cache.Store(machineId, BuildMetadataDictionary(capabilities), capabilities.AgentVersion, capabilities.SupportedServices);
            hydrated++;
        }

        Log.Information(
            "MachineRuntimeCapabilitiesCacheHydrator hydrated {Count} machine(s) from persisted runtime capabilities.",
            hydrated);
    }

    /// <summary>
    /// Rebuild the metadata dictionary that
    /// <see cref="IMachineRuntimeCapabilitiesCache.Store"/> expects, from the
    /// canonical <see cref="MachineRuntimeCapabilities"/> shape we loaded out
    /// of the DB. Keys MUST match those that
    /// <c>TentacleHealthCheckStrategy</c> passes (the keys are the wire
    /// contract between agent's CapabilitiesResponse and the cache).
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildMetadataDictionary(MachineRuntimeCapabilities capabilities)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["os"] = capabilities.Os ?? string.Empty,
            ["osVersion"] = capabilities.OsVersion ?? string.Empty,
            ["defaultShell"] = capabilities.DefaultShell ?? string.Empty,
            ["installedShells"] = capabilities.InstalledShells ?? string.Empty,
            ["architecture"] = capabilities.Architecture ?? string.Empty,
            ["installedRoles"] = capabilities.InstalledRoles ?? string.Empty
        };
    }
}
