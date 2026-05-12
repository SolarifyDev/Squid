using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Deployments;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Pipeline-tier E2E coverage for the <c>Squid.DeployToIISWebSite</c> action.
/// <see cref="DeploymentPipelineFixture{T}"/> swaps the real execution strategy for
/// <see cref="CapturingExecutionStrategy"/>, so these tests verify the SERVER-SIDE
/// pipeline shape — handler → intent → renderer → ScriptExecutionRequest — without
/// requiring a real Windows host or IIS install. Real-cluster execution is deferred
/// to a follow-up phase (per the IIS implementation plan).
///
/// <para>Both Tentacle communication styles (Polling + Listening) are covered via
/// <c>[InlineData]</c>. The IIS step is Windows-only at runtime, but the pipeline
/// dispatch is identical for both — the only difference is how Halibut delivers the
/// captured script to the agent. <see cref="CapturingExecutionStrategy"/> doesn't
/// care about agent OS, so both rows assert the same content.</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class IISDeployPipelineE2ETests
    : IClassFixture<DeploymentPipelineFixture<IISDeployPipelineE2ETests>>
{
    private readonly DeploymentPipelineFixture<IISDeployPipelineE2ETests> _fixture;

    public IISDeployPipelineE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<IISDeployPipelineE2ETests> fixture)
    {
        _ = cluster;   // required by [Collection("KindCluster")] but unused — this test never touches K8s
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    // ── Captured script shape ─────────────────────────────────────────────

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    public async Task FullPipeline_DeployToIISWebSite_CapturesPowerShellWithPreambleAndBody(string communicationStyle)
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedIISWebSiteAsync(
            communicationStyle,
            properties: new Dictionary<string, string>
            {
                ["Squid.Action.IISWebSite.CreateOrUpdateWebSite"] = "True",
                ["Squid.Action.IISWebSite.WebSiteName"] = "OrderApi",
                ["Squid.Action.IISWebSite.ApplicationPoolName"] = "OrderApi-Pool",
                ["Squid.Action.IISWebSite.ApplicationPoolIdentityType"] = "ApplicationPoolIdentity",
                ["Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion"] = "v4.0",
                ["Squid.Action.IISWebSite.WebRoot"] = @"C:\inetpub\OrderApi",
                ["Squid.Action.IISWebSite.Bindings"] = "[{\"protocol\":\"http\",\"port\":\"80\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]",
                ["Squid.Action.IISWebSite.StartApplicationPool"] = "True",
                ["Squid.Action.IISWebSite.StartWebSite"] = "True"
            });

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(1);
        var captured = ExecutionCapture.CapturedRequests[0];

        // The renderer hands a PowerShell-syntax request to whichever transport executes it.
        captured.Syntax.ShouldBe(ScriptSyntax.PowerShell);

        // Preamble lands first — server-rendered hashtable population.
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebSiteName'] = 'OrderApi'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.ApplicationPoolName'] = 'OrderApi-Pool'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebRoot'] = 'C:\\inetpub\\OrderApi'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.CreateOrUpdateWebSite'] = 'True'");

        // Embedded body — Octopus-mirrored deploy logic.
        captured.ScriptBody.ShouldContain("$DeployIISScriptBlock = {");
        captured.ScriptBody.ShouldContain("Import-Module WebAdministration");
        captured.ScriptBody.ShouldContain("netsh http add sslcert");        // present even though the binding is http-only — the body has the SSL branch unconditionally
        captured.ScriptBody.ShouldContain("appcmd.exe");                    // authentication toggle branch
        captured.ScriptBody.ShouldContain("Octopus-IIS-Metabase-Mutex");    // global mutex name preserved verbatim from the Octopus port
    }

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    public async Task FullPipeline_DeployToIISWebSite_BindingsJson_RenderedOnSingleLineInPreamble(string communicationStyle)
    {
        // Realistic operator scenario: HTTPS binding with cert reference + SNI. The Bindings
        // JSON in the action property is multi-line as authored. The server must collapse it
        // to one line in the preamble (the `$SquidParameters['…'] = '…'` assignment can't
        // span multiple lines without breaking the PowerShell parser).
        ExecutionCapture.Clear();

        var bindingsJson = "[\n  {\n    \"protocol\":\"https\",\n    \"port\":\"443\",\n    \"host\":\"api.example.com\",\n    \"requireSni\":true,\n    \"thumbprint\":\"ABCD1234\",\n    \"enabled\":true\n  }\n]";

        var serverTaskId = await SeedIISWebSiteAsync(
            communicationStyle,
            properties: new Dictionary<string, string>
            {
                ["Squid.Action.IISWebSite.CreateOrUpdateWebSite"] = "True",
                ["Squid.Action.IISWebSite.WebSiteName"] = "ApiSite",
                ["Squid.Action.IISWebSite.ApplicationPoolName"] = "ApiSite-Pool",
                ["Squid.Action.IISWebSite.Bindings"] = bindingsJson
            });

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        var captured = ExecutionCapture.CapturedRequests.Single();

        var bindingsLine = captured.ScriptBody
            .Split('\n')
            .Single(l => l.Contains("$SquidParameters['Squid.Action.IISWebSite.Bindings'] ="));

        bindingsLine.ShouldContain("\"protocol\":\"https\"");
        bindingsLine.ShouldContain("\"port\":\"443\"");
        bindingsLine.ShouldContain("\"host\":\"api.example.com\"");
        bindingsLine.ShouldContain("\"requireSni\":true");
        bindingsLine.ShouldContain("\"thumbprint\":\"ABCD1234\"");
    }

    // ── Theory matrix coverage of Tentacle communication-style dispatch ───

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    public async Task FullPipeline_DeployToIISWebSite_DispatchesToTentacle_OfBothStyles(string communicationStyle)
    {
        // Sanity: the IIS action type was added to both TentaclePollingTransport.Capability
        // and TentacleListeningTransport.Capability. The capability validator should let the
        // dispatch through for both styles. Without this wiring the pipeline would
        // short-circuit with a CapabilityViolation.
        ExecutionCapture.Clear();

        var serverTaskId = await SeedIISWebSiteAsync(
            communicationStyle,
            properties: new Dictionary<string, string>
            {
                ["Squid.Action.IISWebSite.CreateOrUpdateWebSite"] = "True",
                ["Squid.Action.IISWebSite.WebSiteName"] = "OrderApi",
                ["Squid.Action.IISWebSite.ApplicationPoolName"] = "OrderApi-Pool"
            });

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);
        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty(
            "Capability validator should accept Squid.DeployToIISWebSite for both " +
            "TentaclePolling and TentacleListening; if this assertion fails check " +
            "the SupportedActionTypes wiring in both transports.");
    }

    // ── Seeder ─────────────────────────────────────────────────────────────

    private async Task<int> SeedIISWebSiteAsync(string communicationStyle, Dictionary<string, string> properties)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy to IIS").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "windows-iis")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "IIS WebSite",
                actionType: "Squid.DeployToIISWebSite").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "windows-iis").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                properties.Select(p => (p.Key, p.Value)).ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync($"E2E IIS Env {suffix}").ConfigureAwait(false);

            var endpointJson = communicationStyle == "TentaclePolling"
                ? JsonSerializer.Serialize(new
                {
                    CommunicationStyle = "TentaclePolling",
                    SubscriptionId = $"sub-{suffix}",
                    Thumbprint = $"E2E-IIS-POLLING-THUMBPRINT-{suffix}"
                })
                : JsonSerializer.Serialize(new
                {
                    CommunicationStyle = "TentacleListening",
                    Uri = $"https://localhost:10933/",
                    Thumbprint = $"E2E-IIS-LISTENING-THUMBPRINT-{suffix}"
                });

            var machine = new Machine
            {
                Name = $"E2E IIS Target {suffix}",
                IsDisabled = false,
                Roles = DeploymentTargetFinder.SerializeRoles(new[] { "windows-iis" }),
                EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"e2e-iis-target-{suffix}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = $"E2E IIS Deployment {suffix}",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = $"E2E IIS Task {suffix}",
                Description = "E2E IIS deploy",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
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

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

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
