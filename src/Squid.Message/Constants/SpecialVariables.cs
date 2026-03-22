namespace Squid.Message.Constants;

public static class SpecialVariables
{
    public static class ActionTypes
    {
        public const string Script = "Squid.Script";
        public const string KubernetesRunScript = "Squid.KubernetesRunScript";
        public const string KubernetesDeployRawYaml = "Squid.KubernetesDeployRawYaml";
        public const string KubernetesDeployContainers = "Squid.KubernetesDeployContainers";
        public const string HelmChartUpgrade = "Squid.HelmChartUpgrade";
        public const string KubernetesDeployIngress = "Squid.KubernetesDeployIngress";
        public const string KubernetesDeployService = "Squid.KubernetesDeployService";
        public const string KubernetesDeployConfigMap = "Squid.KubernetesDeployConfigMap";
        public const string KubernetesDeploySecret = "Squid.KubernetesDeploySecret";
        public const string TentaclePackage = "Squid.TentaclePackage";
        public const string HttpRequest = "Squid.HttpRequest";
        public const string Manual = "Squid.Manual";
        public const string HealthCheck = "Squid.HealthCheck";
        public const string DeployRelease = "Squid.DeployRelease";
        public const string DeployIngress = "Squid.DeployIngress";
        public const string KubernetesKustomize = "Squid.KubernetesKustomize";
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
        public const string MaxParallelism = "Squid.Step.MaxParallelism";
        public const string Timeout = "Squid.Step.Timeout";
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

    public static class Deployment
    {
        public const string Id = "Squid.Deployment.Id";
    }

    public static class Project
    {
        public const string Id = "Squid.Project.Id";
        public const string Name = "Squid.Project.Name";
    }

    public static class Release
    {
        public const string Number = "Squid.Release.Number";
    }

    public static class Environment
    {
        public const string Id = "Squid.Environment.Id";
        public const string Name = "Squid.Environment.Name";
    }

    public static class Machine
    {
        public const string Id = "Squid.Machine.Id";
        public const string Name = "Squid.Machine.Name";
        public const string Roles = "Squid.Machine.Roles";
    }

    public static class Account
    {
        public const string AccountType = "Squid.Account.AccountType";
        public const string CredentialsJson = "Squid.Account.CredentialsJson";
    }

    public static class Kubernetes
    {
        public const string ClusterUrl = "Squid.Action.Kubernetes.ClusterUrl";
        public const string SkipTlsVerification = "Squid.Action.Kubernetes.SkipTlsVerification";
        public const string ClusterCertificate = "Squid.Action.Kubernetes.ClusterCertificate";
        public const string OutputKubectlVersion = "Squid.Action.Kubernetes.OutputKubectlVersion";
        public const string CustomKubectlExecutable = "Squid.Action.Kubernetes.CustomKubectlExecutable";
        public const string Namespace = "Squid.Action.Kubernetes.Namespace";
        public const string SuppressEnvironmentLogging = "Squid.Action.Script.SuppressEnvironmentLogging";
        public const string PrintEvaluatedVariables = "SquidPrintEvaluatedVariables";
        public const string ContainerImage = "ContainerImage";
    }

    public static class Certificate
    {
        public const string ThumbprintSuffix = ".Thumbprint";
        public const string SubjectCommonNameSuffix = ".SubjectCommonName";
        public const string PfxSuffix = ".Pfx";
        public const string NotAfterSuffix = ".NotAfter";
        public const string HasPrivateKeySuffix = ".HasPrivateKey";
    }

    public static class Output
    {
        public static string Variable(string stepName, string variableName)
            => $"Squid.Action[{stepName}].Output.{variableName}";
    }
}
