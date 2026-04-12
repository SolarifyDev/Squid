namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Well-known feature identifiers used by intents in
/// <see cref="ExecutionIntent.RequiredCapabilities"/> and matched against
/// <c>ITransportCapabilities.OptionalFeatures</c> by <c>ICapabilityValidator</c>
/// (introduced in Phase 5.5).
///
/// <para>
/// Keys are intentionally short lowercase strings so they can be written verbatim in handler
/// code and matched case-insensitively at validation time.
/// </para>
/// </summary>
public static class IntentCapabilityKeys
{
    // --- Shell / runtime ------------------------------------------------
    public const string Bash = "bash";
    public const string PowerShell = "powershell";
    public const string Python = "python";
    public const string Sh = "sh";

    // --- Tooling --------------------------------------------------------
    public const string Kubectl = "kubectl";
    public const string Helm = "helm";
    public const string Docker = "docker";
    public const string Kustomize = "kustomize";
    public const string Calamari = "calamari";

    // --- Host features --------------------------------------------------
    public const string Sudo = "sudo";
    public const string NestedFiles = "nested-files";
    public const string ExecutableFlag = "executable-flag";

    // --- Deployment features --------------------------------------------
    public const string PackageStaging = "package-staging";
    public const string RuntimeBundle = "runtime-bundle";
    public const string OutputVariables = "output-variables";
    public const string ArtifactCollection = "artifact-collection";
    public const string IsolationMutex = "isolation-mutex";

    /// <summary>The complete set of well-known capability keys. Used by drift tests.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Bash,
        PowerShell,
        Python,
        Sh,
        Kubectl,
        Helm,
        Docker,
        Kustomize,
        Calamari,
        Sudo,
        NestedFiles,
        ExecutableFlag,
        PackageStaging,
        RuntimeBundle,
        OutputVariables,
        ArtifactCollection,
        IsolationMutex
    };
}
