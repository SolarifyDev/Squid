namespace Squid.Core.Services.OctopusImport.Mapping;

internal static class OctopusPropertyNames
{
    public const string ActionTargetRoles = "Octopus.Action.TargetRoles";
    public const string ActionRunOnServer = "Octopus.Action.RunOnServer";
    public const string ActionConditionExpression = "Octopus.Action.ConditionExpression";
    public const string ActionMaxParallelism = "Octopus.Action.MaxParallelism";
    public const string ActionTimeout = "Octopus.Action.Timeout";
    public const string ActionPackageFeedId = "Octopus.Action.Package.FeedId";
    public const string ActionPackageId = "Octopus.Action.Package.PackageId";
    public const string ActionPackageVersion = "Octopus.Action.Package.PackageVersion";
    public const string ActionScriptBody = "Octopus.Action.Script.ScriptBody";
    public const string ActionScriptSyntax = "Octopus.Action.Script.Syntax";
    public const string ActionScriptSource = "Octopus.Action.Script.ScriptSource";
    public const string ActionManualInstructions = "Octopus.Action.Manual.Instructions";
    public const string ActionManualResponsibleTeamIds = "Octopus.Action.Manual.ResponsibleTeamIds";
    public const string ActionKubernetesResourceStatusCheck = "Octopus.Action.Kubernetes.ResourceStatusCheck";
    public const string ActionKubernetesDeploymentTimeout = "Octopus.Action.Kubernetes.DeploymentTimeout";
    public const string ActionEnabledFeatures = "Octopus.Action.EnabledFeatures";
    public const string StepConditionExpression = "Octopus.Step.ConditionExpression";
    public const string KubernetesContainersPrefix = "Octopus.Action.KubernetesContainers.";
    public const string KubernetesPrefix = "Octopus.Action.Kubernetes.";
}
