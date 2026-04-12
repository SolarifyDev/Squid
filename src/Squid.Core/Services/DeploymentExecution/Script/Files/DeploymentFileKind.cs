namespace Squid.Core.Services.DeploymentExecution.Script.Files;

/// <summary>
/// Semantic category of a deployment file. Used by transports to decide on placement,
/// permissions, and staging strategy (e.g. bootstrap scripts get chmod +x, runtime
/// bundles are injected into the script's PATH, packages are staged via cache).
/// </summary>
public enum DeploymentFileKind
{
    /// <summary>User or handler-generated script file (bash, ps1, py, etc.).</summary>
    Script = 0,

    /// <summary>Arbitrary payload bytes — YAML manifests, Helm values, JSON configs, etc.</summary>
    Asset = 1,

    /// <summary>Package archive staged for extraction on the target (nupkg, tar.gz, zip).</summary>
    Package = 2,

    /// <summary>Bootstrap script injected by the transport (e.g. variable exports, chdir).</summary>
    Bootstrap = 3,

    /// <summary>Runtime helper bundle (set_squidvariable / new_squidartifact / fail_step).</summary>
    RuntimeBundle = 4
}
