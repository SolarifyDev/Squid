using System.Reflection;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Commands.Machine;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Upgrade strategy for both Listening and Polling Linux Tentacles. Sends an
/// embedded bash script over the existing Halibut RPC channel — same plumbing
/// the deployment pipeline already uses for "Run a Script" steps, so we get
/// the resilience, log streaming, and timeout behaviour of that path for
/// free.
///
/// <para>Atomicity: the script downloads → backs up → swaps → verifies → optionally
/// rolls back. See <c>Resources/Upgrade/upgrade-linux-tentacle.sh</c>.</para>
///
/// <para>Idempotency: the script holds a per-version lock file at
/// <c>/var/lib/squid-tentacle/upgrade-&lt;version&gt;.lock</c>; a redelivery
/// (e.g. server-side retry, polling reconnect) is a no-op.</para>
/// </summary>
public sealed class LinuxTentacleUpgradeStrategy : IMachineUpgradeStrategy
{
    private const string EmbeddedScriptResource = "Squid.Core.Resources.Upgrade.upgrade-linux-tentacle.sh";
    private const string DefaultInstallDir = "/opt/squid-tentacle";
    private const string DefaultServiceName = "squid-tentacle";
    private const string DefaultServiceUser = "squid-tentacle";
    private static readonly TimeSpan UpgradeScriptTimeout = TimeSpan.FromMinutes(5);

    private static readonly Lazy<string> _scriptTemplate = new(LoadEmbeddedScript);

    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly IHalibutScriptObserver _observer;
    private readonly IBundledTentacleVersionProvider _versionProvider;

    public LinuxTentacleUpgradeStrategy(
        IHalibutClientFactory halibutClientFactory,
        IHalibutScriptObserver observer,
        IBundledTentacleVersionProvider versionProvider)
    {
        _halibutClientFactory = halibutClientFactory;
        _observer = observer;
        _versionProvider = versionProvider;
    }

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.TentaclePolling)
        || communicationStyle == nameof(CommunicationStyle.TentacleListening);

    public async Task<MachineUpgradeOutcome> UpgradeAsync(Machine machine, string targetVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
            return Failed("targetVersion is required for LinuxTentacle upgrade");

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);
        if (endpoint == null)
            return Failed($"Machine '{machine.Name}' has no usable Halibut endpoint — cannot dispatch upgrade");

        // RID detection lives in the script (uname -m); the URL in the command
        // is parameterised by RID via a simple split after download — this lets
        // a single template work on both x64 and arm64 nodes without needing to
        // know the agent's architecture server-side.
        // For now we hard-code linux-x64 in the URL and the script switches it
        // inline using uname; see the bash sed lines below.
        var downloadUrlTemplate = _versionProvider.GetDownloadUrl(targetVersion, "{RID}");
        // Empty SHA256 = skip verification (script treats empty as opt-out).
        // Phase 2 will plumb a release-time-generated hash through the
        // version provider so we get end-to-end tarball authenticity.
        var scriptBody = BuildScript(targetVersion, downloadUrlTemplate, expectedSha256: string.Empty);

        var ticketId = $"upgrade-{machine.Id}-{Guid.NewGuid():N}"[..32];
        var ticket = new ScriptTicket(ticketId);

        var command = new StartScriptCommand(
            ticket,
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            UpgradeScriptTimeout,
            null,
            Array.Empty<string>(),
            ticketId,
            TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.Bash
        };

        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        Log.Information("[Upgrade] Dispatching upgrade to {Machine} → version {Version}", machine.Name, targetVersion);

        try
        {
            var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

            var result = await _observer.ObserveAndCompleteAsync(
                machine,
                scriptClient,
                ticket,
                UpgradeScriptTimeout,
                ct,
                masker: null,
                initialStartResponse: startResponse,
                endpoint: endpoint).ConfigureAwait(false);

            if (result.Success)
            {
                return new MachineUpgradeOutcome
                {
                    Status = MachineUpgradeStatus.Upgraded,
                    Detail = $"Upgrade to {targetVersion} reported success in {result.LogLines?.Count ?? 0} log lines"
                };
            }

            return new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.Failed,
                Detail = $"Upgrade script failed (exit {result.ExitCode}). Last log: " +
                         $"{(result.LogLines is { Count: > 0 } ll ? ll[^1] : "(no log lines)")}"
            };
        }
        catch (HalibutClientException ex)
        {
            // Halibut disconnect mid-script is EXPECTED — the agent restarts
            // the squid-tentacle service as part of the upgrade. Treat as
            // Initiated; the next health check will confirm whether the new
            // version came up.
            Log.Information("[Upgrade] Halibut disconnect during upgrade of {Machine} (expected on service restart): {Reason}",
                machine.Name, ex.Message);
            return new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.Initiated,
                Detail = $"Upgrade dispatched; agent disconnected mid-script as expected during restart. Verify outcome via next health check."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Upgrade] Unexpected error upgrading {Machine}", machine.Name);
            return Failed($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildScript(string targetVersion, string downloadUrlTemplate, string expectedSha256)
    {
        var template = _scriptTemplate.Value;

        return template
            .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
            // {RID} stays inside the URL and is rewritten by the script's
            // `case "$ARCH"` block — keeps server agnostic to agent arch.
            .Replace("{{DOWNLOAD_URL}}", downloadUrlTemplate.Replace("{RID}", "$RID", StringComparison.Ordinal), StringComparison.Ordinal)
            // Empty when the release pipeline hasn't published a hash yet —
            // script treats empty as "skip verification" rather than fail.
            // Phase 2: publish hashes alongside the tarball + plumb through.
            .Replace("{{EXPECTED_SHA256}}", expectedSha256 ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{INSTALL_DIR}}", DefaultInstallDir, StringComparison.Ordinal)
            .Replace("{{SERVICE_NAME}}", DefaultServiceName, StringComparison.Ordinal)
            .Replace("{{SERVICE_USER}}", DefaultServiceUser, StringComparison.Ordinal);
    }

    private static string LoadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedScriptResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedScriptResource}' not found. " +
                "Verify Squid.Core.csproj has <EmbeddedResource Include=\"Resources\\Upgrade\\*\" />.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static MachineUpgradeOutcome Failed(string detail) => new()
    {
        Status = MachineUpgradeStatus.Failed,
        Detail = detail
    };
}
