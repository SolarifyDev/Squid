using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.E2ETests.Deployments;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesCredentialTypeE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesCredentialTypeE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesCredentialTypeE2ETests> _fixture;

    public KubernetesCredentialTypeE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesCredentialTypeE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData(AccountType.Token)]
    [InlineData(AccountType.UsernamePassword)]
    [InlineData(AccountType.ClientCertificate)]
    public async Task FullPipeline_CredentialType_FlowsThroughPipeline(AccountType accountType)
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedDatabaseAsync(accountType);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var request = ExecutionCapture.CapturedRequests.First();

        // Verify endpoint context contains the correct account type
        request.EndpointContext.ShouldNotBeNull();
        var accountData = request.EndpointContext.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(accountType);

        // Verify script body is wrapped (contains credential setup)
        request.ScriptBody.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task FullPipeline_WithClusterCertificate_FlowsThroughPipeline()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedDatabaseWithCertificateAsync();

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();
        var request = ExecutionCapture.CapturedRequests.First();
        request.EndpointContext.ShouldNotBeNull();

        // Verify cluster certificate is present in the endpoint context (decoded from base64 to PEM text)
        var clusterCert = request.EndpointContext.GetCertificate(EndpointResourceType.ClusterCertificate);
        clusterCert.ShouldNotBeNullOrEmpty();
        clusterCert.ShouldBe("-----BEGIN CERTIFICATE-----\nE2ECLUSTERCA\n-----END CERTIFICATE-----");
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private async Task<int> SeedDatabaseAsync(AccountType accountType)
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork);

            await seeder.SeedAsync(
                createFeedSecrets: false,
                replicas: 1,
                namespaceName: "default",
                communicationStyle: "KubernetesApi",
                accountType: accountType).ConfigureAwait(false);

            serverTaskId = seeder.ServerTaskId;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task<int> SeedDatabaseWithCertificateAsync()
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork);

            await seeder.SeedAsync(
                createFeedSecrets: false,
                replicas: 1,
                namespaceName: "default",
                communicationStyle: "KubernetesApi").ConfigureAwait(false);

            serverTaskId = seeder.ServerTaskId;

            // Add a cluster certificate (base64 of PEM text, matching real upload flow)
            const string pemText = "-----BEGIN CERTIFICATE-----\nE2ECLUSTERCA\n-----END CERTIFICATE-----";
            var cert = new Certificate
            {
                Name = "E2E Cluster CA",
                CertificateData = Convert.ToBase64String(Encoding.UTF8.GetBytes(pemText)),
                CertificateDataFormat = CertificateDataFormat.Pem,
                HasPrivateKey = false,
                SpaceId = 1
            };

            await repository.InsertAsync(cert).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Update machine endpoint to include cluster cert reference via providers
        await _fixture.Run<IMachineDataProvider, ICertificateDataProvider>(async (machineProvider, certProvider) =>
        {
            var (_, machines) = await machineProvider.GetMachinePagingAsync().ConfigureAwait(false);
            var machine = machines.First();

            var (_, certs) = await certProvider.GetCertificatePagingAsync().ConfigureAwait(false);
            var cert = certs.First(c => c.Name == "E2E Cluster CA");

            machine.Endpoint = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://localhost:6443",
                SkipTlsVerification = "True",
                Namespace = "default",
                ResourceReferences = new object[]
                {
                    new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 },
                    new { Type = (int)EndpointResourceType.ClusterCertificate, ResourceId = cert.Id }
                }
            });

            await machineProvider.UpdateMachineAsync(machine).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    // ========================================================================
    // Assertions
    // ========================================================================

    private async Task AssertTaskStateAsync(string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);
            tasks.ShouldNotBeNull();
            tasks.Count.ShouldBeGreaterThanOrEqualTo(1);

            var task = tasks.OrderByDescending(t => t.Id).First();
            task.State.ShouldBe(expectedState, $"Expected task state '{expectedState}' but was '{task.State}'");
        }).ConfigureAwait(false);
    }
}
