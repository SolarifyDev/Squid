namespace Squid.Core.Services.OctopusImport.Octopus;

internal readonly record struct OctopusResourceMetadata(
    int Rank,
    bool IsOrderable,
    bool IsReportOutOfScope,
    bool IsPreviewOutOfScope,
    bool IsUnsupported)
{
    private static readonly IReadOnlyDictionary<OctopusResourceKind, OctopusResourceMetadata> Metadata =
        new Dictionary<OctopusResourceKind, OctopusResourceMetadata>
        {
            [OctopusResourceKind.Unknown] = new(1000, false, false, false, true),
            [OctopusResourceKind.ProjectGroup] = new(10, true, false, false, false),
            [OctopusResourceKind.Environment] = new(20, true, false, false, false),
            [OctopusResourceKind.Lifecycle] = new(30, true, false, false, false),
            [OctopusResourceKind.LifecyclePhase] = new(40, true, false, false, false),
            [OctopusResourceKind.Feed] = new(50, true, false, false, false),
            [OctopusResourceKind.Team] = new(60, true, false, false, true),
            [OctopusResourceKind.Machine] = new(70, true, false, false, true),
            [OctopusResourceKind.Account] = new(80, true, false, false, false),
            [OctopusResourceKind.Certificate] = new(90, true, false, false, true),
            [OctopusResourceKind.Project] = new(100, true, false, false, false),
            [OctopusResourceKind.Channel] = new(110, true, false, false, false),
            [OctopusResourceKind.DeploymentSettings] = new(120, true, false, false, false),
            [OctopusResourceKind.DeploymentProcess] = new(130, true, false, false, false),
            [OctopusResourceKind.DeploymentStep] = new(140, true, false, false, false),
            [OctopusResourceKind.DeploymentAction] = new(150, true, false, false, false),
            [OctopusResourceKind.VariableSet] = new(160, true, false, false, false),
            [OctopusResourceKind.Variable] = new(170, true, false, false, false),
            [OctopusResourceKind.Release] = new(180, true, false, false, false),
            [OctopusResourceKind.ActionTemplate] = new(190, true, false, false, true),
            [OctopusResourceKind.Tenant] = new(200, true, false, false, true),
            [OctopusResourceKind.Runbook] = new(210, true, false, false, true),
            [OctopusResourceKind.Trigger] = new(220, true, false, false, true),
            [OctopusResourceKind.DeploymentProcessSnapshot] = new(900, true, true, true, false),
            [OctopusResourceKind.VariableSetSnapshot] = new(910, true, true, true, false),
            [OctopusResourceKind.Deployment] = new(930, true, false, false, false),
            [OctopusResourceKind.ServerTask] = new(940, true, false, false, false),
            [OctopusResourceKind.WorkerPool] = new(950, false, true, true, false)
        };

    public static OctopusResourceMetadata For(OctopusResourceKind kind)
        => Metadata.TryGetValue(kind, out var metadata)
            ? metadata
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Octopus resource kind.");

    public bool IsCurrentConfiguration(OctopusResourceNode resource)
        => IsOrderable &&
           (!resource.IsHistorical ||
            resource.Kind is OctopusResourceKind.Release
                or OctopusResourceKind.Deployment
                or OctopusResourceKind.ServerTask);
}
