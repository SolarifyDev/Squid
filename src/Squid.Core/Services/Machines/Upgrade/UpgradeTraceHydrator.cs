namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Hydrates the in-memory <see cref="IUpgradeEventTimelineStore"/> from the
/// <c>machine.last_upgrade_trace_json</c> column at server startup, so a freshly
/// deployed pod doesn't show operators an empty upgrade timeline for machines
/// whose most recent upgrade concluded before the restart.
///
/// <para>Also primes the <see cref="IUpgradeTracePersistenceGate"/> with each
/// hydrated snapshot's signature, so the first post-restart Capabilities probe
/// doesn't re-persist a terminal outcome that's already on disk.</para>
///
/// <para>Autofac calls <see cref="Start"/> once at container build time on every
/// <see cref="IStartable"/> singleton. We block on the async load inline —
/// startup is single-threaded and there's no benefit to deferring the warm-up.
/// A DB read failure logs + continues with an empty cache (strictly better than
/// blocking server startup on a transient DB issue); the next terminal upgrade
/// per machine repopulates the row.</para>
/// </summary>
public sealed class UpgradeTraceHydrator : IStartable
{
    private readonly IComponentContext _container;

    /// <summary>
    /// Take the container (not the services directly) because
    /// <see cref="IUpgradeTracePersistence"/> is scoped
    /// (<see cref="IScopedDependency"/>) — we open a lifetime scope for the
    /// one-off startup query, then dispose it. The store + gate are singletons
    /// resolved from the root context.
    /// </summary>
    public UpgradeTraceHydrator(IComponentContext container)
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
            Log.Warning(ex,
                "UpgradeTraceHydrator failed to load persisted upgrade traces — server will start with an " +
                "empty in-memory upgrade timeline cache and repopulate per machine from the next terminal upgrade.");
        }
    }

    private async Task HydrateAsync(CancellationToken ct)
    {
        var store = _container.Resolve<IUpgradeEventTimelineStore>();
        var gate = _container.Resolve<IUpgradeTracePersistenceGate>();

        // Persistence is scoped (IScopedDependency) so we MUST open a lifetime
        // scope to resolve it (it depends on the EF DbContext / unit-of-work).
        await using var scope = _container.Resolve<ILifetimeScope>().BeginLifetimeScope();

        var persistence = scope.Resolve<IUpgradeTracePersistence>();

        var rows = await persistence.LoadAllAsync(ct).ConfigureAwait(false);

        var hydrated = ApplyTo(store, gate, rows);

        Log.Information("UpgradeTraceHydrator hydrated {Count} machine(s) from persisted upgrade traces.", hydrated);
    }

    /// <summary>
    /// Pure hydrate step: replay each persisted snapshot into the in-memory
    /// store (status + events + log) and prime the dedup gate so the snapshot
    /// already on disk isn't re-persisted on the first post-restart probe.
    /// Public so it can be exercised by tests (unit + integration) without an
    /// Autofac container — it has no instance state and no side effects beyond
    /// the two collaborators passed in.
    /// </summary>
    public static int ApplyTo(IUpgradeEventTimelineStore store, IUpgradeTracePersistenceGate gate, IReadOnlyList<(int MachineId, UpgradeTraceSnapshot Snapshot)> snapshots)
    {
        var hydrated = 0;

        foreach (var (machineId, snapshot) in snapshots)
        {
            if (snapshot == null) continue;

            store.StoreStatus(machineId, snapshot.Status);
            store.Store(machineId, snapshot.Events);
            store.StoreLog(machineId, snapshot.Log);

            gate.MarkPersisted(machineId, snapshot.Signature);

            hydrated++;
        }

        return hydrated;
    }
}
