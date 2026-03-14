using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Squid.Core.Services.DeploymentExecution.Filtering;

namespace Squid.E2ETests.Helpers;

public class K8sTestDataSeeder
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TestDataBuilder _builder;

    public int ServerTaskId { get; private set; }

    public K8sTestDataSeeder(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _builder = new TestDataBuilder(repository, unitOfWork);
    }

    public async Task SeedAsync(
        bool createFeedSecrets,
        int replicas = 2,
        string namespaceName = "default",
        string communicationStyle = "KubernetesApi",
        string agentSubscriptionId = null,
        string agentThumbprint = null,
        CancellationToken ct = default)
    {
        var variableSet = await _builder.CreateVariableSetAsync().ConfigureAwait(false);

        await _builder.CreateVariablesAsync(variableSet.Id,
            ("Namespace", namespaceName, VariableType.String, false),
            ("Replicas", replicas.ToString(), VariableType.String, false),
            ("AppEnv", "e2e-test", VariableType.String, false),
            ("DbHost", "db.example.com", VariableType.String, false)).ConfigureAwait(false);

        var project = await _builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
        await _builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

        var process = await _builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        await _builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

        var step = await _builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy K8s Containers").ConfigureAwait(false);
        await _builder.CreateStepPropertiesAsync(step.Id,
            ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

        var action = await _builder.CreateDeploymentActionAsync(
            step.Id, 1, "Deploy demo",
            actionType: "Squid.KubernetesDeployContainers").ConfigureAwait(false);

        await _builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);

        var containerJson = BuildContainerJson(createFeedSecrets);
        var configMapValues = BuildConfigMapValues();

        await _builder.CreateActionPropertiesAsync(action.Id,
            ("Squid.Action.KubernetesContainers.DeploymentName", "demo-nginx"),
            ("Squid.Action.KubernetesContainers.Namespace", "#{Namespace}"),
            ("Squid.Action.KubernetesContainers.Replicas", "#{Replicas}"),
            ("Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate"),
            ("Squid.Action.KubernetesContainers.Containers", containerJson),
            ("Squid.Action.KubernetesContainers.ServiceName", "demo-service"),
            ("Squid.Action.KubernetesContainers.ServiceType", "ClusterIP"),
            ("Squid.Action.KubernetesContainers.ServicePorts",
                "[{\"name\":\"http\",\"port\":\"80\",\"targetPort\":\"80\",\"nodePort\":\"\",\"protocol\":\"TCP\"}]"),
            ("Squid.Action.KubernetesContainers.ConfigMapName", "demo-config"),
            ("Squid.Action.KubernetesContainers.ConfigMapValues", configMapValues),
            ("Squid.Action.KubernetesContainers.CombinedVolumes",
                "[{\"Name\":\"data-vol\",\"Type\":\"EmptyDir\",\"ReferenceName\":\"\"}]"),
            ("Squid.Action.KubernetesContainers.DeploymentAnnotations", "[]"),
            ("Squid.Action.KubernetesContainers.DeploymentLabels", "{}"),
            ("Squid.Action.KubernetesContainers.PodAnnotations", "[]"),
            ("Squid.Action.KubernetesContainers.Tolerations", "[]"),
            ("Squid.Action.KubernetesContainers.NodeAffinity", "[]"),
            ("Squid.Action.KubernetesContainers.PodAffinity", "[]"),
            ("Squid.Action.KubernetesContainers.PodAntiAffinity", "[]"),
            ("Squid.Action.KubernetesContainers.DnsConfigOptions", "[]"),
            ("Squid.Action.KubernetesContainers.PodSecuritySysctls", "[]")).ConfigureAwait(false);

        var channel = await _builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

        var environment = await _builder.CreateEnvironmentAsync("E2E Test Environment").ConfigureAwait(false);

        var machine = communicationStyle == "KubernetesAgent"
            ? CreateAgentMachine(environment, agentSubscriptionId, agentThumbprint)
            : CreateApiMachine(environment);

        await _repository.InsertAsync(machine, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        if (communicationStyle != "KubernetesAgent")
            await CreateAccountAsync(ct).ConfigureAwait(false);

        var feed = new ExternalFeed
        {
            Name = "DockerHub",
            Slug = "dockerhub",
            FeedType = "Docker",
            FeedUri = "https://index.docker.io/v2",
            RegistryPath = "docker.io",
            Username = "testuser",
            Password = "testpass",
            SpaceId = 1
        };

        await _repository.InsertAsync(feed, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var release = await _builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

        var selectedPackage = new ReleaseSelectedPackage
        {
            ReleaseId = release.Id,
            ActionName = "Deploy demo",
            Version = "1.0.0"
        };

        await _repository.InsertAsync(selectedPackage, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var deployment = new Deployment
        {
            Name = "E2E Test Deployment",
            SpaceId = 1,
            ChannelId = channel.Id,
            ProjectId = project.Id,
            ReleaseId = release.Id,
            EnvironmentId = environment.Id,
            DeployedBy = 1,
            Created = DateTimeOffset.UtcNow,
            Json = string.Empty
        };

        await _repository.InsertAsync(deployment, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var serverTask = new ServerTask
        {
            Name = "E2E Deployment Task",
            Description = "E2E test deployment task",
            QueueTime = DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            SpaceId = 1,
            LastModified = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            BatchId = 0,
            JSON = string.Empty,
            HasWarningsOrErrors = false,
            ServerNodeId = Guid.NewGuid(),
            DurationSeconds = 0,
            DataVersion = Array.Empty<byte>()
        };

        await _repository.InsertAsync(serverTask, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        deployment.TaskId = serverTask.Id;
        await _repository.UpdateAsync(deployment, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        ServerTaskId = serverTask.Id;
    }

    private static Machine CreateApiMachine(Environment environment)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = new[]
            {
                new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
            }
        });

        return new Machine
        {
            Name = "E2E K8s Target",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
            Json = "{\"Endpoint\":{\"Uri\":\"https://localhost:10933\",\"Thumbprint\":\"E2E-THUMBPRINT\"}}",
            Thumbprint = "E2E-THUMBPRINT",
            Uri = "https://localhost:10933",
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            OperatingSystem = OperatingSystemType.Windows,
            ShellName = "PowerShell",
            ShellVersion = "7.0",
            LicenseHash = string.Empty,
            Slug = "e2e-k8s-target"
        };
    }

    private static Machine CreateAgentMachine(
        Environment environment,
        string subscriptionId = null,
        string thumbprint = null)
    {
        subscriptionId ??= Guid.NewGuid().ToString("N");
        thumbprint ??= "E2E-AGENT-THUMBPRINT";

        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = thumbprint,
            Namespace = "default"
        });

        return new Machine
        {
            Name = "E2E K8s Agent",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
            Json = string.Empty,
            Thumbprint = thumbprint,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            PollingSubscriptionId = subscriptionId,
            LicenseHash = string.Empty,
            Slug = $"e2e-k8s-agent-{subscriptionId[..8]}"
        };
    }

    private async Task CreateAccountAsync(CancellationToken ct)
    {
        var account = new DeploymentAccount
        {
            SpaceId = 1,
            Name = "E2E K8s Account",
            Slug = "e2e-k8s-account",
            AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(
                new TokenCredentials { Token = "e2e-test-token" })
        };

        await _repository.InsertAsync(account, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string BuildContainerJson(bool createFeedSecrets)
    {
        return JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "demo-nginx",
                ["PackageId"] = "library/nginx",
                ["FeedId"] = 1,
                ["Ports"] = new[] { new { key = "http", value = "80", option = "TCP" } },
                ["EnvironmentVariables"] = Array.Empty<object>(),
                ["SecretEnvironmentVariables"] = Array.Empty<object>(),
                ["ConfigMapEnvironmentVariables"] = Array.Empty<object>(),
                ["FieldRefEnvironmentVariables"] = Array.Empty<object>(),
                ["ConfigMapEnvFromSource"] = new[] { new { key = "demo-config", value = "", option = "" } },
                ["SecretEnvFromSource"] = Array.Empty<object>(),
                ["VolumeMounts"] = new[] { new { key = "data-vol", value = "/data", option = "" } },
                ["Resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new { memory = "128Mi", cpu = "100m", ephemeralStorage = "" },
                    ["limits"] = new { memory = "256Mi", cpu = "200m", ephemeralStorage = "", nvidiaGpu = "", amdGpu = "" }
                },
                ["LivenessProbe"] = BuildEmptyProbe(),
                ["ReadinessProbe"] = BuildEmptyProbe(),
                ["StartupProbe"] = BuildEmptyProbe(),
                ["Command"] = Array.Empty<string>(),
                ["Args"] = Array.Empty<string>(),
                ["IsInitContainer"] = "False",
                ["SecurityContext"] = BuildEmptySecurityContext(),
                ["Lifecycle"] = new Dictionary<string, object>(),
                ["CreateFeedSecrets"] = createFeedSecrets ? "True" : "False"
            }
        });
    }

    private static string BuildConfigMapValues()
    {
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["APP_ENV"] = "#{AppEnv}",
            ["DB_HOST"] = "#{DbHost}",
            ["LOG_LEVEL"] = "info"
        });
    }

    private static Dictionary<string, object> BuildEmptyProbe() => new()
    {
        ["failureThreshold"] = "",
        ["initialDelaySeconds"] = "",
        ["periodSeconds"] = "",
        ["successThreshold"] = "",
        ["timeoutSeconds"] = "",
        ["type"] = (object)null,
        ["exec"] = new { command = Array.Empty<string>() },
        ["httpGet"] = new { host = "", path = "", port = "", scheme = "", httpHeaders = Array.Empty<object>() },
        ["tcpSocket"] = new { host = "", port = "" }
    };

    private static Dictionary<string, object> BuildEmptySecurityContext() => new()
    {
        ["allowPrivilegeEscalation"] = "",
        ["privileged"] = "",
        ["readOnlyRootFilesystem"] = "",
        ["runAsGroup"] = "",
        ["runAsNonRoot"] = "",
        ["runAsUser"] = "",
        ["capabilities"] = new { add = Array.Empty<string>(), drop = Array.Empty<string>() },
        ["seLinuxOptions"] = new { level = "", role = "", type = "", user = "" }
    };
}
