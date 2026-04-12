using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public sealed record TransportCapabilities : ITransportCapabilities
{
    public IReadOnlySet<ScriptSyntax> SupportedSyntaxes { get; init; } = EmptySyntaxes;

    public bool SupportsNestedFiles { get; init; }

    public bool SupportsExecutableFlag { get; init; }

    public long? MaxFileSizeBytes { get; init; }

    public PackageStagingMode PackageStagingModes { get; init; } = PackageStagingMode.None;

    public ExecutionLocation ExecutionLocation { get; init; } = ExecutionLocation.Unspecified;

    public ExecutionBackend ExecutionBackend { get; init; } = ExecutionBackend.Unspecified;

    public bool SupportsOutputVariables { get; init; }

    public bool SupportsArtifactCollection { get; init; }

    public bool SupportsSudo { get; init; }

    public bool SupportsIsolationMutex { get; init; }

    public bool RequiresContextPreparationForPackagedPayload { get; init; }

    public IReadOnlySet<string> SupportedActionTypes { get; init; } = EmptyStrings;

    public IReadOnlySet<string> OptionalFeatures { get; init; } = EmptyStrings;

    private static readonly IReadOnlySet<ScriptSyntax> EmptySyntaxes = new HashSet<ScriptSyntax>();
    private static readonly IReadOnlySet<string> EmptyStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<ScriptSyntax> Syntaxes(params ScriptSyntax[] syntaxes) =>
        new HashSet<ScriptSyntax>(syntaxes);

    public static IReadOnlySet<string> ActionTypes(params string[] actionTypes) =>
        new HashSet<string>(actionTypes, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Features(params string[] features) =>
        new HashSet<string>(features, StringComparer.OrdinalIgnoreCase);
}
