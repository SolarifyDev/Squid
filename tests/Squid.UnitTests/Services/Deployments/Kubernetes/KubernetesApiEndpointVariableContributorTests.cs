using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiEndpointVariableContributorTests
{
    private readonly KubernetesApiEndpointVariableContributor _contributor = new();

    private static string MakeEndpointJson(
        string clusterUrl = "https://k8s.example.com:6443",
        string ns = "default",
        string skipTls = "False",
        string deploymentAccountId = "42",
        string clusterCert = null,
        string certificateId = null) =>
        JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = clusterUrl,
            Namespace = ns,
            SkipTlsVerification = skipTls,
            DeploymentAccountId = deploymentAccountId,
            ClusterCertificate = clusterCert,
            CertificateId = certificateId
        });

    private static EndpointContext TokenContext() => new()
    {
        EndpointJson = MakeEndpointJson(),
        AccountType = AccountType.Token,
        CredentialsJson = JsonSerializer.Serialize(new TokenCredentials { Token = "test-token-123" })
    };

    private static EndpointContext UsernamePasswordContext() => new()
    {
        EndpointJson = MakeEndpointJson(),
        AccountType = AccountType.UsernamePassword,
        CredentialsJson = JsonSerializer.Serialize(new UsernamePasswordCredentials { Username = "admin", Password = "s3cret" })
    };

    private static EndpointContext ClientCertContext() => new()
    {
        EndpointJson = MakeEndpointJson(),
        AccountType = AccountType.ClientCertificate,
        CredentialsJson = JsonSerializer.Serialize(new ClientCertificateCredentials { ClientCertificateData = "LS0tLS1CRUdJTi...", ClientCertificateKeyData = "LS0tLS1CRUdJTi...KEY" })
    };

    private static EndpointContext AwsContext() => new()
    {
        EndpointJson = MakeEndpointJson(),
        AccountType = AccountType.AmazonWebServicesAccount,
        CredentialsJson = JsonSerializer.Serialize(new AwsCredentials { AccessKey = "AKIAIOSFODNN7EXAMPLE", SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY" })
    };

    // === ParseResourceReferences ===

    [Fact]
    public void ParseResourceReferences_ValidJson_ReturnsAccountId()
    {
        var json = MakeEndpointJson(deploymentAccountId: "42");
        _contributor.ParseResourceReferences(json).DeploymentAccountId.ShouldBe(42);
    }

    [Fact]
    public void ParseResourceReferences_WithCertificateId_ReturnsCertificateId()
    {
        var json = MakeEndpointJson(certificateId: "99");
        _contributor.ParseResourceReferences(json).CertificateId.ShouldBe(99);
    }

    [Fact]
    public void ParseResourceReferences_NoAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson(deploymentAccountId: null);
        _contributor.ParseResourceReferences(json).DeploymentAccountId.ShouldBeNull();
    }

    [Fact]
    public void ParseResourceReferences_NonNumericAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson(deploymentAccountId: "abc");
        _contributor.ParseResourceReferences(json).DeploymentAccountId.ShouldBeNull();
    }

    [Fact]
    public void ParseResourceReferences_EmptyAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson(deploymentAccountId: "");
        _contributor.ParseResourceReferences(json).DeploymentAccountId.ShouldBeNull();
    }

    [Fact]
    public void ParseResourceReferences_InvalidJson_ReturnsEmpty()
    {
        var refs = _contributor.ParseResourceReferences("not-json");
        refs.DeploymentAccountId.ShouldBeNull();
        refs.CertificateId.ShouldBeNull();
    }

    [Fact]
    public void ParseResourceReferences_EmptyString_ReturnsEmpty()
    {
        var refs = _contributor.ParseResourceReferences(string.Empty);
        refs.DeploymentAccountId.ShouldBeNull();
    }

    [Fact]
    public void ParseResourceReferences_Null_ReturnsEmpty()
    {
        var refs = _contributor.ParseResourceReferences(null);
        refs.DeploymentAccountId.ShouldBeNull();
    }

    // === ContributeVariables — count & all names ===

    [Fact]
    public void ContributeVariables_ValidEndpoint_Returns9Variables()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.Count.ShouldBe(9);
    }

    [Fact]
    public void ContributeVariables_AllExpectedVariableNamesPresent()
    {
        var vars = _contributor.ContributeVariables(TokenContext());
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldContain("Squid.Action.Kubernetes.ClusterUrl");
        names.ShouldContain("Squid.Account.AccountType");
        names.ShouldContain("Squid.Account.CredentialsJson");
        names.ShouldContain("Squid.Action.Kubernetes.SkipTlsVerification");
        names.ShouldContain("Squid.Action.Kubernetes.Namespace");
        names.ShouldContain("Squid.Action.Kubernetes.ClusterCertificate");
        names.ShouldContain("Squid.Action.Script.SuppressEnvironmentLogging");
        names.ShouldContain("Squid.Action.Kubernetes.OutputKubectlVersion");
        names.ShouldContain("SquidPrintEvaluatedVariables");
    }

    // === ContributeVariables — endpoint field values ===

    [Fact]
    public void ContributeVariables_ClusterUrl_MappedCorrectly()
    {
        var ctx = TokenContext();
        ctx.EndpointJson = MakeEndpointJson(clusterUrl: "https://my-cluster:6443");
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.ClusterUrl" && v.Value == "https://my-cluster:6443");
    }

    [Fact]
    public void ContributeVariables_Namespace_MappedCorrectly()
    {
        var ctx = TokenContext();
        ctx.EndpointJson = MakeEndpointJson(ns: "production");
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.Namespace" && v.Value == "production");
    }

    [Fact]
    public void ContributeVariables_SkipTls_MappedCorrectly()
    {
        var ctx = TokenContext();
        ctx.EndpointJson = MakeEndpointJson(skipTls: "True");
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.SkipTlsVerification" && v.Value == "True");
    }

    [Fact]
    public void ContributeVariables_ClusterCertificate_MappedCorrectly()
    {
        var ctx = TokenContext();
        ctx.EndpointJson = MakeEndpointJson(clusterCert: "MIICpDCCAYwCCQ...");
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.ClusterCertificate" && v.Value == "MIICpDCCAYwCCQ...");
    }

    // === ContributeVariables — account types ===

    [Fact]
    public void ContributeVariables_TokenAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "Token");
    }

    [Fact]
    public void ContributeVariables_UsernamePasswordAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(UsernamePasswordContext());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "UsernamePassword");
    }

    [Fact]
    public void ContributeVariables_ClientCertAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(ClientCertContext());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "ClientCertificate");
    }

    [Fact]
    public void ContributeVariables_AwsAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(AwsContext());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "AmazonWebServicesAccount");
    }

    // === ContributeVariables — CredentialsJson ===

    [Fact]
    public void ContributeVariables_TokenAccount_CredentialsJsonContainsToken()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        var credVar = vars.First(v => v.Name == "Squid.Account.CredentialsJson");
        credVar.Value.ShouldContain("test-token-123");
    }

    [Fact]
    public void ContributeVariables_UsernamePasswordAccount_CredentialsJsonContainsCreds()
    {
        var vars = _contributor.ContributeVariables(UsernamePasswordContext());

        var credVar = vars.First(v => v.Name == "Squid.Account.CredentialsJson");
        credVar.Value.ShouldContain("admin");
        credVar.Value.ShouldContain("s3cret");
    }

    [Fact]
    public void ContributeVariables_ClientCertAccount_CredentialsJsonContainsCertData()
    {
        var vars = _contributor.ContributeVariables(ClientCertContext());

        var credVar = vars.First(v => v.Name == "Squid.Account.CredentialsJson");
        credVar.Value.ShouldContain("LS0tLS1CRUdJTi...");
    }

    [Fact]
    public void ContributeVariables_AwsAccount_CredentialsJsonContainsAwsCreds()
    {
        var vars = _contributor.ContributeVariables(AwsContext());

        var credVar = vars.First(v => v.Name == "Squid.Account.CredentialsJson");
        credVar.Value.ShouldContain("AKIAIOSFODNN7EXAMPLE");
    }

    // === ContributeVariables — null/empty account ===

    [Fact]
    public void ContributeVariables_NullAccount_DefaultsToToken()
    {
        var ctx = new EndpointContext { EndpointJson = MakeEndpointJson() };
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "Token");
    }

    [Fact]
    public void ContributeVariables_NullAccount_CredentialsJsonEmpty()
    {
        var ctx = new EndpointContext { EndpointJson = MakeEndpointJson() };
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Account.CredentialsJson" && v.Value == string.Empty);
    }

    // === ContributeVariables — default endpoint values ===

    [Fact]
    public void ContributeVariables_NullNamespace_DefaultsToDefault()
    {
        var json = JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://k8s:6443",
            Namespace = null
        });

        var ctx = TokenContext();
        ctx.EndpointJson = json;
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.Namespace" && v.Value == "default");
    }

    [Fact]
    public void ContributeVariables_NullSkipTls_DefaultsToFalse()
    {
        var json = JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://k8s:6443",
            SkipTlsVerification = null
        });

        var ctx = TokenContext();
        ctx.EndpointJson = json;
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.SkipTlsVerification" && v.Value == "False");
    }

    // === ContributeVariables — static/fixed variables ===

    [Fact]
    public void ContributeVariables_SuppressEnvironmentLogging_AlwaysFalse()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.ShouldContain(v => v.Name == "Squid.Action.Script.SuppressEnvironmentLogging" && v.Value == "False");
    }

    [Fact]
    public void ContributeVariables_OutputKubectlVersion_AlwaysTrue()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.OutputKubectlVersion" && v.Value == "True");
    }

    [Fact]
    public void ContributeVariables_PrintEvaluatedVariables_AlwaysTrue()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.ShouldContain(v => v.Name == "SquidPrintEvaluatedVariables" && v.Value == "True");
    }

    // === ContributeVariables — bad input ===

    [Fact]
    public void ContributeVariables_InvalidJson_ReturnsEmpty()
    {
        var ctx = new EndpointContext { EndpointJson = "not-json", AccountType = AccountType.Token };
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_EmptyJson_ReturnsEmpty()
    {
        var ctx = new EndpointContext { EndpointJson = string.Empty, AccountType = AccountType.Token };
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_NullJson_ReturnsEmpty()
    {
        var ctx = new EndpointContext { EndpointJson = null, AccountType = AccountType.Token };
        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldBeEmpty();
    }

    // === ContributeVariables — sensitive variable marking ===

    [Fact]
    public void ContributeVariables_CredentialsJson_IsSensitive()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.ShouldContain(v => v.Name == "Squid.Account.CredentialsJson" && v.IsSensitive);
    }

    [Fact]
    public void ContributeVariables_NonSensitiveVariables_NotMarkedSensitive()
    {
        var vars = _contributor.ContributeVariables(TokenContext());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.ClusterUrl" && !v.IsSensitive);
        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && !v.IsSensitive);
    }

    // === ContributeAdditionalVariablesAsync — ContainerImage ===

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_ValidFeed_ReturnsContainerImageVariable()
    {
        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://registry.example.com/v2" });

        var contributor = new KubernetesApiEndpointVariableContributor(feedProvider.Object);

        var snapshot = MakeSnapshotWithFeed(feedId: 10, packageId: "myapp/backend");
        var release = new Release { Version = "1.2.3" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage"
            && v.Value == "registry.example.com/myapp/backend:1.2.3");
    }

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_NoActionWithFeed_FallsBackToVersion()
    {
        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var contributor = new KubernetesApiEndpointVariableContributor(feedProvider.Object);

        var snapshot = MakeSnapshotWithoutFeed();
        var release = new Release { Version = "2.0.0" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage" && v.Value == "2.0.0");
    }

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_FeedNotFound_FallsBackToVersion()
    {
        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed)null);

        var contributor = new KubernetesApiEndpointVariableContributor(feedProvider.Object);

        var snapshot = MakeSnapshotWithFeed(feedId: 99, packageId: "myapp");
        var release = new Release { Version = "3.0.0" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage" && v.Value == "3.0.0");
    }

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_NullSnapshot_FallsBackToVersion()
    {
        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var contributor = new KubernetesApiEndpointVariableContributor(feedProvider.Object);

        var release = new Release { Version = "4.0.0" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(null, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage" && v.Value == "4.0.0");
    }

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_NoFeedProvider_FallsBackToVersion()
    {
        // Contributor created without feed provider (parameterless constructor)
        var contributor = new KubernetesApiEndpointVariableContributor();

        var snapshot = MakeSnapshotWithFeed(feedId: 10, packageId: "myapp");
        var release = new Release { Version = "5.0.0" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage" && v.Value == "5.0.0");
    }

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_FeedWithRegistryPath_UsesRegistryPath()
    {
        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 10,
                FeedUri = "https://index.docker.io/v2",
                RegistryPath = "docker.io"
            });

        var contributor = new KubernetesApiEndpointVariableContributor(feedProvider.Object);

        var snapshot = MakeSnapshotWithFeed(feedId: 10, packageId: "library/nginx");
        var release = new Release { Version = "1.25.0" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage"
            && v.Value == "docker.io/library/nginx:1.25.0");
    }

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_FeedWithNonStandardPort_IncludesPort()
    {
        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 10,
                FeedUri = "https://registry.internal.com:5000/v2"
            });

        var contributor = new KubernetesApiEndpointVariableContributor(feedProvider.Object);

        var snapshot = MakeSnapshotWithFeed(feedId: 10, packageId: "myapp");
        var release = new Release { Version = "2.0.0" };

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldContain(v => v.Name == "ContainerImage"
            && v.Value == "registry.internal.com:5000/myapp:2.0.0");
    }

    private static DeploymentProcessSnapshotDto MakeSnapshotWithFeed(int feedId, string packageId)
    {
        return new DeploymentProcessSnapshotDto
        {
            Id = 1, OriginalProcessId = 1, Version = 1,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots = new List<DeploymentStepSnapshotDataDto>
                {
                    new()
                    {
                        Id = 1, Name = "Step1", StepType = "Action", StepOrder = 1,
                        ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
                        {
                            new()
                            {
                                Id = 1, Name = "Deploy", ActionType = "Octopus.KubernetesDeployContainers",
                                ActionOrder = 1, FeedId = feedId, PackageId = packageId
                            }
                        }
                    }
                }
            }
        };
    }

    private static DeploymentProcessSnapshotDto MakeSnapshotWithoutFeed()
    {
        return new DeploymentProcessSnapshotDto
        {
            Id = 1, OriginalProcessId = 1, Version = 1,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots = new List<DeploymentStepSnapshotDataDto>
                {
                    new()
                    {
                        Id = 1, Name = "Step1", StepType = "Action", StepOrder = 1,
                        ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
                        {
                            new()
                            {
                                Id = 1, Name = "Script", ActionType = "Octopus.Script",
                                ActionOrder = 1, FeedId = null, PackageId = null
                            }
                        }
                    }
                }
            }
        };
    }
}
