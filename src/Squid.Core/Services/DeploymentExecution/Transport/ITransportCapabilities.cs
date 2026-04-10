using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface ITransportCapabilities
{
    IReadOnlySet<ScriptSyntax> SupportedSyntaxes { get; }

    bool SupportsNestedFiles { get; }

    bool SupportsExecutableFlag { get; }

    long? MaxFileSizeBytes { get; }

    PackageStagingMode PackageStagingModes { get; }

    ExecutionLocation ExecutionLocation { get; }

    ExecutionBackend ExecutionBackend { get; }

    bool SupportsOutputVariables { get; }

    bool SupportsArtifactCollection { get; }

    bool SupportsSudo { get; }

    bool SupportsIsolationMutex { get; }

    bool RequiresContextPreparationForPackagedPayload { get; }

    IReadOnlySet<string> SupportedActionTypes { get; }

    IReadOnlySet<string> OptionalFeatures { get; }
}
