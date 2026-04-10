namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// A transport-agnostic package reference carried on an <see cref="ExecutionIntent"/>.
/// Uniquely identifies a package that must be staged on the target before script
/// execution, but intentionally does NOT carry bytes, local paths, or acquisition state —
/// the intent layer is pre-acquisition.
///
/// <para>
/// Used by <see cref="ExecutionIntent.Packages"/> and by <c>DeployPackageIntent.Package</c>.
/// </para>
/// </summary>
public sealed record IntentPackageReference
{
    /// <summary>The package identifier as known to the feed (e.g. <c>Acme.Web</c>).</summary>
    public required string PackageId { get; init; }

    /// <summary>The exact version to deploy (e.g. <c>1.2.3</c>).</summary>
    public required string Version { get; init; }

    /// <summary>The external feed identifier this package comes from.</summary>
    public required string FeedId { get; init; }

    /// <summary>Optional human-readable purpose hint (<c>primary</c>, <c>sidecar-config</c>, ...).</summary>
    public string PurposeHint { get; init; } = string.Empty;
}
