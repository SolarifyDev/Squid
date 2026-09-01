using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to stage a package on the target, extract it into a durable installation
/// directory, and run package conventions (PreDeploy / PostDeploy).
///
/// <para>
/// The renderer consults acquisition results + transport capabilities and materialises
/// a Tentacle Calamari <c>deploy-package</c> request or an SSH durable-install script.
/// Package identity and version come from Release selection, not latest resolution.
/// </para>
/// </summary>
public sealed record DeployPackageIntent : ExecutionIntent
{
    public required IntentPackageReference Package { get; init; }

    /// <summary><c>Versioned</c> or <c>Custom</c>.</summary>
    public string InstallationDirectoryMode { get; init; } = "Versioned";

    /// <summary>
    /// Custom absolute installation directory when mode is <c>Custom</c>.
    /// May still contain <c>#{variable}</c> tokens before IntentVariableExpander runs.
    /// </summary>
    public string CustomInstallationDirectory { get; init; } = string.Empty;

    public required PackageInstallationPathSegments PathSegments { get; init; }

    public ScriptSyntax ScriptSyntax { get; init; } = ScriptSyntax.Bash;
}
