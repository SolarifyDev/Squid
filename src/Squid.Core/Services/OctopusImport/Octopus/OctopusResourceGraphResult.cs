using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed record OctopusResourceGraph(
    IReadOnlyList<OctopusResourceNode> Resources,
    IReadOnlyList<OctopusResourceReference> References,
    IReadOnlyList<OctopusResourceDependency> Dependencies,
    IReadOnlyList<OctopusInputExtractionDiagnostic> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);

    /// <summary>The Octopus installation the export or history bundle came from (manifest <c>InstallationId</c>); null for exports that do not record it.</summary>
    public string InstallationId { get; init; }

    /// <summary>The export id suffix carried by the documents' <c>SpaceId</c> (export-source fingerprint); null for REST bundles and unsuffixed files.</summary>
    public string ExportSourceId { get; init; }
}

public sealed record OctopusResourceNode(
    string SourceId,
    string Name,
    OctopusResourceKind Kind,
    OctopusDocumentKind DocumentKind,
    string SourcePath,
    string OwnerProjectId,
    string ParentSourceId,
    bool IsHistorical,
    object Source)
{
    public T GetSource<T>() where T : class => Source as T;
}

public sealed record OctopusResourceReference(
    string FromSourceId,
    OctopusResourceKind FromKind,
    OctopusResourceReferenceKind ReferenceKind,
    string ToSourceId,
    OctopusResourceKind? ToKind,
    string OwnerProjectId,
    bool IsRequired,
    bool CreatesDependency);

public sealed record OctopusResourceDependency(
    string SourceId,
    string DependsOnSourceId,
    OctopusResourceReferenceKind ReferenceKind,
    OctopusResourceKind? DependsOnKind);
