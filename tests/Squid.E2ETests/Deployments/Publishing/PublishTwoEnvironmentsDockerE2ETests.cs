using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Recipes;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Commands.Publishing;
using Squid.Message.Enums.Deployments;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Publishing;

/// <summary>
/// Fidelity tier: HIGH. The same app is published to two environments of one space through the real publish service (each with its own port, which publishing stores as a
/// project variable scoped to that environment), each queued deployment is run by the real <c>DeploymentTaskExecutor</c>, and the exact script the executor hands the
/// target for each environment, with every variable already substituted by the real pipeline, is run under real bash against the real Docker daemon. Only the Tentacle is
/// replaced (the capturing strategy answers the deploy-package step with the extraction folder), so the two ports, the two project/environment ids and the two container
/// names are all the product's own. Skipped when there is no Docker, no bash, or on Windows.
/// </summary>
[Trait("Category", "PublishRecipeDockerE2E")]
[Collection(PublishCollections.Pipeline)]
public class PublishTwoEnvironmentsDockerE2ETests : IClassFixture<DeploymentPipelineFixture<PublishTwoEnvironmentsDockerE2ETests>>, IDisposable
{
    private const string Role = "web";

    private static readonly HttpClient Http = new();

    private readonly DeploymentPipelineFixture<PublishTwoEnvironmentsDockerE2ETests> _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-e2e-two-env-{Guid.NewGuid():N}");
    private readonly string _packageId = $"e2e-{Guid.NewGuid():N}"[..16];
    private readonly List<string> _containers = new();

    public PublishTwoEnvironmentsDockerE2ETests(DeploymentPipelineFixture<PublishTwoEnvironmentsDockerE2ETests> fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var name in _containers.SelectMany(n => new[] { n, n + "-candidate", n + "-previous" }))
            Docker("rm", "-f", name);

        foreach (var name in _containers)
            foreach (var image in Docker("images", name, "-q").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Docker("rmi", "-f", image);

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static byte[] App(string content)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("app/Dockerfile", "FROM busybox\nCOPY www /www\nEXPOSE 8080\nCMD [\"httpd\",\"-f\",\"-p\",\"8080\",\"-h\",\"/www\"]\n"), ("app/www/index.html", content) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));

                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task TheSameApp_PublishedToTwoEnvironments_RunsOnTwoPorts_BothStayUp_AndRedeployingOneLeavesTheOther()
    {
        if (!DockerAvailable()) return;

        var (spaceId, staging, production) = await SeedAsync();
        var stagingPort = FreePort();
        var productionPort = FreePort();

        await PushAsync(spaceId, App("version one"), "1.0.0");
        await PushAsync(spaceId, App("version two"), "2.0.0");

        var first = await PublishAsync(spaceId, staging, "1.0.0", stagingPort);
        var second = await PublishAsync(spaceId, production, "1.0.0", productionPort);

        second.ProjectId.ShouldBe(first.ProjectId, "one project, one shared process");
        second.ReleaseId.ShouldBe(first.ReleaseId);

        var stagingContainer = ContainerName(first.ProjectId, staging);
        var productionContainer = ContainerName(first.ProjectId, production);

        _containers.Add(stagingContainer);
        _containers.Add(productionContainer);

        var stagingRun = await RunDeploymentAsync(first, "1.0.0");
        var productionRun = await RunDeploymentAsync(second, "1.0.0");

        stagingRun.ExitCode.ShouldBe(0, stagingRun.Output);
        productionRun.ExitCode.ShouldBe(0, productionRun.Output);

        (await GetAsync(stagingPort)).ShouldBe("version one");
        (await GetAsync(productionPort)).ShouldBe("version one");
        stagingContainer.ShouldNotBe(productionContainer);
        Containers().ShouldBe(new[] { stagingContainer, productionContainer }.Order(StringComparer.Ordinal).ToArray(), "one container per environment");

        // Publishing the next version to staging only replaces staging's container.
        var next = await PublishAsync(spaceId, staging, "2.0.0", stagingPort);
        var nextRun = await RunDeploymentAsync(next, "2.0.0");

        nextRun.ExitCode.ShouldBe(0, nextRun.Output);
        (await GetAsync(stagingPort)).ShouldBe("version two");
        (await GetAsync(productionPort)).ShouldBe("version one", "deploying staging must not replace production's container");
        Containers().ShouldBe(new[] { stagingContainer, productionContainer }.Order(StringComparer.Ordinal).ToArray());
    }

    private string ContainerName(int projectId, int environmentId) => $"{ContainerAppPlan.ContainerNameBase(_packageId)}-p{projectId}-e{environmentId}";

    private string[] Containers() => Docker("ps", "-a", "--format", "{{.Names}}", "--filter", $"name={ContainerAppPlan.ContainerNameBase(_packageId)}").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToArray();

    private async Task<PublishPackageResponseData> PublishAsync(int spaceId, int environmentId, string version, int port)
    {
        var command = new PublishPackageCommand { SpaceId = spaceId, PackageId = _packageId, Version = version, EnvironmentId = environmentId, TargetRoles = [Role], Inputs = new() { ["hostPort"] = port.ToString() } };

        return await _fixture.Run<IPublishService, PublishPackageResponseData>(s => s.PublishAsync(command, CancellationToken.None));
    }

    private async Task PushAsync(int spaceId, byte[] bytes, string version)
    {
        var push = new PushBuiltInPackageCommand { SpaceId = spaceId, FileName = $"{_packageId}.{version}.zip", Version = version, Content = new MemoryStream(bytes), DeclaredSizeBytes = bytes.Length };

        await _fixture.Run<IBuiltInPackagePushService, PushBuiltInPackageResponseData>(s => s.PushAsync(push, CancellationToken.None));

        ZipFile.ExtractToDirectory(new MemoryStream(bytes), Path.Combine(_root, version));
    }

    // Runs the queued deployment through the real executor, takes the script it handed the target for the second step, and runs that script under bash.
    private async Task<ScriptRun> RunDeploymentAsync(PublishPackageResponseData published, string version)
    {
        _fixture.ExecutionCapture.Clear();
        _fixture.ExecutionCapture.ResultFactory = request => request.CalamariCommand == "deploy-package"
            ? new ScriptExecutionResult { Success = true, LogLines = [$"##squid[setVariable name='Squid.Action.Package.InstallationDirectoryPath' value='{Path.Combine(_root, version)}']"] }
            : new ScriptExecutionResult { Success = true };

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(published.TaskId, CancellationToken.None));

        var script = _fixture.ExecutionCapture.CapturedRequests.Single(r => r.CalamariCommand != "deploy-package").ScriptBody;
        var path = Path.Combine(_root, $"deploy-{published.TaskId}.sh");

        await File.WriteAllTextAsync(path, script);

        var acquired = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(published.DeploymentId);

        if (Directory.Exists(acquired)) Directory.Delete(acquired, recursive: true);

        return Bash(path);
    }

    private static async Task<string> GetAsync(int port)
    {
        using var response = await Http.GetAsync($"http://127.0.0.1:{port}/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET / on port {port}");

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<(int SpaceId, int Staging, int Production)> SeedAsync()
    {
        var store = await _fixture.Run<IPackageStoreService, int>(async s =>
        {
            var created = await s.CreateAsync(new CreatePackageStoreCommand { Name = $"E2E {Guid.NewGuid():N}"[..20], Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = Path.Combine(_root, "store") } }, CancellationToken.None);

            await s.SetDefaultAsync(created.Id, CancellationToken.None);

            return created.Id;
        });

        store.ShouldBeGreaterThan(0);

        int spaceId = 0, staging = 0, production = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var slug = $"e2e{Guid.NewGuid():N}"[..12];
            var space = new Squid.Core.Persistence.Entities.Deployments.Space { Name = slug, Slug = slug, Json = "{}" };

            await repository.InsertAsync(space);
            await unitOfWork.SaveChangesAsync();

            spaceId = space.Id;

            await repository.InsertAsync(new ProjectGroup { Name = "Apps", Slug = $"apps-{slug}", Description = "Apps", SpaceId = spaceId });

            var stagingEnvironment = new Environment { Name = "Staging", Slug = $"staging-{slug}", SpaceId = spaceId, SortOrder = 0 };
            var productionEnvironment = new Environment { Name = "Production", Slug = $"production-{slug}", SpaceId = spaceId, SortOrder = 1 };
            var lifecycle = new Lifecycle { Name = "Default", Slug = $"default-{slug}", SpaceId = spaceId };

            await repository.InsertAsync(stagingEnvironment);
            await repository.InsertAsync(productionEnvironment);
            await repository.InsertAsync(lifecycle);
            await unitOfWork.SaveChangesAsync();

            staging = stagingEnvironment.Id;
            production = productionEnvironment.Id;

            var phase = new LifecyclePhase { LifecycleId = lifecycle.Id, Name = "All", SortOrder = 0 };

            await repository.InsertAsync(phase);
            await unitOfWork.SaveChangesAsync();

            foreach (var environmentId in new[] { staging, production })
            {
                await repository.InsertAsync(new LifecyclePhaseEnvironment { PhaseId = phase.Id, EnvironmentId = environmentId, TargetType = LifecyclePhaseEnvironmentTargetType.Optional });
                await repository.InsertAsync(new Machine { Name = $"web-{environmentId}-{slug}", Slug = $"web-{environmentId}-{slug}", IsDisabled = false, Roles = DeploymentTargetFinder.SerializeRoles([Role]), EnvironmentIds = DeploymentTargetFinder.SerializeIds([environmentId]), SpaceId = spaceId, Endpoint = $$"""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-{{environmentId}}-{{slug}}","Thumbprint":"E2E-TWO-ENV-{{environmentId}}-{{slug}}"}""" });
            }

            await unitOfWork.SaveChangesAsync();
        });

        await _fixture.Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(spaceId, CancellationToken.None));

        return (spaceId, staging, production);
    }

    private static bool DockerAvailable() => !OperatingSystem.IsWindows() && File.Exists("/bin/bash") && Docker("info").ExitCode == 0;

    private sealed record ScriptRun(int ExitCode, string Output);

    private static ScriptRun Docker(params string[] arguments) => Run("docker", arguments, null);

    private static ScriptRun Bash(string script) => Run("/bin/bash", [script], Path.GetDirectoryName(script));

    private static ScriptRun Run(string file, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;

        using var process = Process.Start(info)!;
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{file}' did not finish. Output so far:\n{output}");
        }

        process.WaitForExit();

        lock (output) return new ScriptRun(process.ExitCode, output.ToString());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
