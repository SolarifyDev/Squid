using Squid.Tentacle.Abstractions;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// Adapts <see cref="SelfHealController"/> to the host's
/// <see cref="ITentacleBackgroundTask"/> lifecycle: starts the heal loops when the
/// host launches background tasks and drains them when the host's cancellation
/// token trips on shutdown. This is the wiring that takes the self-heal
/// scaffolding from dead code to live — before it, no flavor scheduled any heal
/// action, so a disk-full agent failed deployments with no auto-reclaim.
/// </summary>
public sealed class SelfHealBackgroundTask : ITentacleBackgroundTask, IAsyncDisposable
{
    private readonly SelfHealController _controller;

    public SelfHealBackgroundTask(SelfHealController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public string Name => "SelfHeal";

    public async Task RunAsync(CancellationToken ct)
    {
        _controller.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        finally
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _controller.DisposeAsync();

    /// <summary>
    /// Builds the disk-pressure self-heal for the local script workspaces that
    /// <see cref="LocalScriptService"/> creates under the temp root
    /// (<c>{tempRoot}/squid-tentacle-{ticketId}</c>). The <paramref name="reporter"/>
    /// (the live script backend) vetoes deletion of any workspace still running a
    /// script, so the sweep can never remove an in-flight deployment's directory.
    /// </summary>
    public static SelfHealBackgroundTask ForLocalWorkspaces(IRunningScriptReporter reporter)
    {
        var stateStoreFactory = new ScriptStateStoreFactory();

        var action = new DiskPressureHealAction(
            workspaceRootProvider: Path.GetTempPath,
            candidateProbe: root => WorkspaceProbe.Probe(root, stateStoreFactory),
            policy: new DefaultWorkspaceCleanupPolicy(),
            removeWorkspace: path => Directory.Delete(path, recursive: true),
            runningScriptReporters: reporter == null ? null : new[] { reporter });

        return new SelfHealBackgroundTask(new SelfHealController(new ISelfHealAction[] { action }));
    }
}
