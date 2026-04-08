namespace Squid.Message.Constants;

public static class SpecialVariables
{
    public static class ActionTypes
    {
        public const string Script = "Squid.Script";
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
        public const string OpenClawInvokeTool = "Squid.OpenClaw.InvokeTool";
        public const string OpenClawRunAgent = "Squid.OpenClaw.RunAgent";
        public const string OpenClawWake = "Squid.OpenClaw.Wake";
        public const string OpenClawWaitSession = "Squid.OpenClaw.WaitSession";
        public const string OpenClawAssert = "Squid.OpenClaw.Assert";
        public const string OpenClawFetchResult = "Squid.OpenClaw.FetchResult";
        public const string OpenClawChatCompletion = "Squid.OpenClaw.ChatCompletion";
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
        public const string StructuredConfigurationVariablesEnabled = "Squid.Action.StructuredConfigurationVariables.Enabled";
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
        public const string Hostname = "Squid.Machine.Hostname";
    }

    public static class Account
    {
        public const string AccountType = "Squid.Account.AccountType";
        public const string CredentialsJson = "Squid.Account.CredentialsJson";
        public const string Token = "Squid.Account.Token";
        public const string Username = "Squid.Account.Username";
        public const string Password = "Squid.Account.Password";
        public const string ClientCertificateData = "Squid.Account.ClientCertificateData";
        public const string ClientCertificateKeyData = "Squid.Account.ClientCertificateKeyData";
        public const string AccessKey = "Squid.Account.AccessKey";
        public const string SecretKey = "Squid.Account.SecretKey";
        public const string SubscriptionNumber = "Squid.Account.SubscriptionNumber";
        public const string ClientId = "Squid.Account.ClientId";
        public const string TenantId = "Squid.Account.TenantId";
        public const string AzureKey = "Squid.Account.AzureKey";
        public const string AzureJwt = "Squid.Account.AzureJwt";
        public const string GcpJsonKey = "Squid.Account.GcpJsonKey";
        public const string RoleArn = "Squid.Account.RoleArn";
        public const string SessionDuration = "Squid.Account.SessionDuration";
        public const string ExternalId = "Squid.Account.ExternalId";
        public const string WebIdentityToken = "Squid.Account.WebIdentityToken";
        public const string SshPrivateKeyFile = "Squid.Account.SshPrivateKeyFile";
        public const string SshPassphrase = "Squid.Account.SshPassphrase";
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

    public static class Ssh
    {
        public const string Host = "Squid.Action.Ssh.Host";
        public const string Port = "Squid.Action.Ssh.Port";
        public const string Fingerprint = "Squid.Action.Ssh.Fingerprint";
        public const string RemoteWorkingDirectory = "Squid.Action.Ssh.RemoteWorkingDirectory";
        public const string ProxyType = "Squid.Action.Ssh.ProxyType";
        public const string ProxyHost = "Squid.Action.Ssh.ProxyHost";
        public const string ProxyPort = "Squid.Action.Ssh.ProxyPort";
        public const string ProxyUsername = "Squid.Action.Ssh.ProxyUsername";
        public const string ProxyPassword = "Squid.Action.Ssh.ProxyPassword";
    }

    public static class OpenClaw
    {
        // Endpoint variables (contributed by OpenClawEndpointVariableContributor)
        public const string BaseUrl = "Squid.Action.OpenClaw.BaseUrl";
        public const string GatewayToken = "Squid.Action.OpenClaw.GatewayToken";
        public const string HooksToken = "Squid.Action.OpenClaw.HooksToken";
        public const string SessionKey = "Squid.Action.OpenClaw.SessionKey";

        // Output variables (emitted by execution strategy)
        public const string ResultJson = "OpenClaw.ResultJson";
        public const string Ok = "OpenClaw.Ok";
        public const string Accepted = "OpenClaw.Accepted";
        public const string Status = "OpenClaw.Status";
        public const string Summary = "OpenClaw.Summary";
        public const string ChatResponse = "OpenClaw.ChatResponse";
        public const string ChatModel = "OpenClaw.ChatModel";
        public const string ChatFinishReason = "OpenClaw.ChatFinishReason";

        // Action properties (DB keys — read from ActionProperties dictionary)
        public const string PropTool = "Squid.Action.OpenClaw.Tool";
        public const string PropToolAction = "Squid.Action.OpenClaw.ToolAction";
        public const string PropArgsJson = "Squid.Action.OpenClaw.ArgsJson";
        public const string PropSessionKey = "Squid.Action.OpenClaw.SessionKey";
        public const string PropTimeoutSeconds = "Squid.Action.OpenClaw.TimeoutSeconds";
        public const string PropMessage = "Squid.Action.OpenClaw.Message";
        public const string PropAgentId = "Squid.Action.OpenClaw.AgentId";
        public const string PropWakeMode = "Squid.Action.OpenClaw.WakeMode";
        public const string PropDeliver = "Squid.Action.OpenClaw.Deliver";
        public const string PropChannel = "Squid.Action.OpenClaw.Channel";
        public const string PropTo = "Squid.Action.OpenClaw.To";
        public const string PropAgentName = "Squid.Action.OpenClaw.AgentName";
        public const string PropModel = "Squid.Action.OpenClaw.Model";
        public const string PropThinking = "Squid.Action.OpenClaw.Thinking";
        public const string PropAgentTimeoutSeconds = "Squid.Action.OpenClaw.AgentTimeoutSeconds";
        public const string PropWakeText = "Squid.Action.OpenClaw.WakeText";
        public const string PropSuccessPattern = "Squid.Action.OpenClaw.SuccessPattern";
        public const string PropFailPattern = "Squid.Action.OpenClaw.FailPattern";
        public const string PropMaxWaitSeconds = "Squid.Action.OpenClaw.MaxWaitSeconds";
        public const string PropPollSeconds = "Squid.Action.OpenClaw.PollSeconds";
        public const string PropJsonPath = "Squid.Action.OpenClaw.JsonPath";
        public const string PropOperator = "Squid.Action.OpenClaw.Operator";
        public const string PropExpected = "Squid.Action.OpenClaw.Expected";
        public const string PropSourceVariable = "Squid.Action.OpenClaw.SourceVariable";
        public const string PropFieldMappings = "Squid.Action.OpenClaw.FieldMappings";
        public const string PropPrompt = "Squid.Action.OpenClaw.Prompt";
        public const string PropSystemPrompt = "Squid.Action.OpenClaw.SystemPrompt";
        public const string PropMessagesJson = "Squid.Action.OpenClaw.MessagesJson";
        public const string PropUser = "Squid.Action.OpenClaw.User";
    }

    public static class Output
    {
        public static string Variable(string stepName, string variableName)
            => $"Squid.Action[{stepName}].Output.{variableName}";

        public static string MachineVariable(string stepName, string machineName, string variableName)
            => $"Squid.Action[{stepName}].Output[{machineName}].{variableName}";
    }
}
