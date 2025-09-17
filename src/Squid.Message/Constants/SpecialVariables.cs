namespace Squid.Message.Constants;

public static class SpecialVariables
{
    public static class ActionTypes
    {
        public const string Script = "Squid.Script";
        public const string KubernetesDeployRawYaml = "Squid.KubernetesDeployRawYaml";
        public const string TentaclePackage = "Squid.TentaclePackage";
        public const string HttpRequest = "Squid.HttpRequest";
        public const string Manual = "Squid.Manual";
        public const string DeployRelease = "Squid.DeployRelease";
        public const string DeployIngress = "Squid.DeployIngress";
    }

    public static class StepTypes
    {
        public const string Manual = "Manual";
        public const string HttpRequest = "HttpRequest";
        public const string DeployRelease = "DeployRelease";
        public const string DeployIngress = "DeployIngress";
        public const string RunScript = "RunScript";
        public const string DeployPackage = "DeployPackage";
    }

    public static class StartTriggers
    {
        public const string StartAfterPrevious = "StartAfterPrevious";
        public const string StartWithPrevious = "StartWithPrevious";
        public const string StartAfterDelay = "StartAfterDelay";
    }

    public static class PackageRequirements
    {
        public const string LetSquidDecide = "LetSquidDecide";
        public const string BeforePackageAcquisition = "BeforePackageAcquisition";
        public const string AfterPackageAcquisition = "AfterPackageAcquisition";
    }

    public static class Action
    {
        public const string ScriptBody = "Squid.Action.Script.ScriptBody";
        public const string ScriptSyntax = "Squid.Action.Script.Syntax";
        public const string ScriptSource = "Squid.Action.Script.ScriptSource";
        public const string PackageFeedId = "Squid.Action.Package.FeedId";
        public const string PackageId = "Squid.Action.Package.PackageId";
        public const string PackageVersion = "Squid.Action.Package.PackageVersion";
        public const string CustomInstallationDirectory = "Squid.Action.Package.CustomInstallationDirectory";
        public const string AdditionalInstallationDirectory = "Squid.Action.Package.AdditionalInstallationDirectory";
        public const string KubernetesYaml = "Squid.Action.KubernetesContainers.CustomResourceYaml";
        public const string KubernetesNamespace = "Squid.Action.KubernetesContainers.Namespace";
        public const string HttpUrl = "Squid.Action.Http.Url";
        public const string HttpMethod = "Squid.Action.Http.Method";
        public const string HttpHeaders = "Squid.Action.Http.Headers";
        public const string HttpBody = "Squid.Action.Http.Body";
        public const string ManualInstructions = "Squid.Action.Manual.Instructions";
        public const string ManualResponsibleTeamIds = "Squid.Action.Manual.ResponsibleTeamIds";
        public const string DeployReleaseProjectId = "Squid.Action.DeployRelease.ProjectId";
        public const string DeployReleaseVersion = "Squid.Action.DeployRelease.Version";
        public const string DeployReleaseChannelId = "Squid.Action.DeployRelease.ChannelId";
    }

    public static class Step
    {
        public const string TargetRoles = "Squid.Action.TargetRoles";
        public const string RunOnServer = "Squid.Action.RunOnServer";
        public const string WorkerPoolId = "Squid.Action.WorkerPoolId";
        public const string Container = "Squid.Action.Container";
        public const string ConditionExpression = "Squid.Step.ConditionExpression";
        public const string RequiredToSucceed = "Squid.Step.RequiredToSucceed";
    }

    public static class ScriptSyntax
    {
        public const string PowerShell = "PowerShell";
        public const string Bash = "Bash";
        public const string CSharp = "CSharp";
        public const string FSharp = "FSharp";
        public const string Python = "Python";
    }

    public static class HttpMethods
    {
        public const string Get = "GET";
        public const string Post = "POST";
        public const string Put = "PUT";
        public const string Delete = "DELETE";
        public const string Patch = "PATCH";
        public const string Head = "HEAD";
        public const string Options = "OPTIONS";
    }
}
