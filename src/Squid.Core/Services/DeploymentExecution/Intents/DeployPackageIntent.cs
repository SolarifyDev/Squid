using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to stage a package on the target, extract it, and optionally run
/// pre- and post-deploy scripts against the extracted root.
///
/// <para>
/// The renderer consults <c>IPackageStagingPlanner</c> (Phase 7) to decide whether to
/// full-upload, cache-hit, remote-download, or delta-stage the package.
/// </para>
/// </summary>
public sealed record DeployPackageIntent : ExecutionIntent
{
    /// <summary>The package to acquire and extract.</summary>
    public required IntentPackageReference Package { get; init; }

    /// <summary>
    /// Relative path under the transport work directory where the package should be
    /// extracted. Empty string means "extract at the work directory root".
    /// </summary>
    public string ExtractTo { get; init; } = string.Empty;

    /// <summary>Optional script to run after extraction but before <see cref="PostDeployScript"/>.</summary>
    public string? PreDeployScript { get; init; }

    /// <summary>Optional script to run after deployment completes successfully.</summary>
    public string? PostDeployScript { get; init; }

    /// <summary>Syntax of both <see cref="PreDeployScript"/> and <see cref="PostDeployScript"/>.</summary>
    public ScriptSyntax ScriptSyntax { get; init; } = ScriptSyntax.Bash;
}
