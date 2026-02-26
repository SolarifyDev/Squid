using System.Net;
using System.Text.Json;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Fakes;
using Squid.Tentacle.Tests.Support.Paths;
using Squid.Tentacle.Tests.Support.Process;
using Squid.Tentacle.Tests.Support.Collections;

namespace Squid.Tentacle.Tests.Integration;

[Collection(TentacleProcessIntegrationCollection.Name)]
public class ProgramStartupSmokeTests : TentacleIntegrationTestBase
{
    [Fact]
    public async Task Program_Exits_With_Error_For_Unknown_Flavor()
    {
        using var sandbox = new TemporaryTentacleDirectory();
        var healthPort = TcpPortAllocator.GetEphemeralPort();

        await using var process = StartTentacleProcess(new Dictionary<string, string>
        {
            ["Tentacle__Flavor"] = "DefinitelyNotAFlavor",
            ["Tentacle__CertsPath"] = sandbox.CertsPath,
            ["Tentacle__WorkspacePath"] = sandbox.WorkspacePath,
            ["Tentacle__HealthCheckPort"] = healthPort.ToString(),
            ["Tentacle__ServerUrl"] = "http://127.0.0.1:65530",
            ["Kubernetes__UseScriptPods"] = "false"
        });

        var exitCode = await process.WaitForExitAsync(TimeSpan.FromSeconds(20), TestCancellationToken);

        exitCode.ShouldNotBe(0);
        process.CombinedOutput.ShouldContain("Unknown Tentacle flavor");
    }

    [Fact]
    public async Task Program_Starts_Registers_And_Reports_Readiness_In_Local_Mode()
    {
        await RunSuccessfulStartupSmokeOnceAsync(machineName: "test-k8s-agent");
    }

    [Fact]
    public async Task Program_StartupSmoke_Is_Stable_Across_Repeated_Runs()
    {
        for (var i = 0; i < 3; i++)
        {
            await RunSuccessfulStartupSmokeOnceAsync(machineName: $"test-k8s-agent-{i}");
        }
    }

    private static RunningCommandProcess StartTentacleProcess(IReadOnlyDictionary<string, string> environment)
    {
        File.Exists(WorkspacePaths.SquidTentacleAssemblyPath)
            .ShouldBeTrue($"Squid.Tentacle assembly not found at {WorkspacePaths.SquidTentacleAssemblyPath}");

        return RunningCommandProcess.Start(
            "dotnet",
            $"\"{WorkspacePaths.SquidTentacleAssemblyPath}\"",
            WorkspacePaths.SquidTentacleProjectDirectory,
            environment);
    }

    private static async Task<bool> WaitForReadyAsync(int port, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/readyz", ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return content.Contains("ready", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (HttpRequestException)
            {
                // Still starting.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Retry until deadline.
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task RunSuccessfulStartupSmokeOnceAsync(string machineName)
    {
        using var sandbox = new TemporaryTentacleDirectory();
        await using var registrationServer = FakeMachineRegistrationServer.Start();

        var pollingPort = TcpPortAllocator.GetEphemeralPort();
        var healthPort = TcpPortAllocator.GetEphemeralPort();

        await using var process = StartTentacleProcess(new Dictionary<string, string>
        {
            ["Tentacle__Flavor"] = "KubernetesAgent",
            ["Tentacle__ServerUrl"] = registrationServer.BaseAddress.ToString().TrimEnd('/'),
            ["Tentacle__ServerCommsUrl"] = $"https://localhost:{pollingPort}/",
            ["Tentacle__BearerToken"] = "unit-test-token",
            ["Tentacle__MachineName"] = machineName,
            ["Tentacle__Roles"] = "web,api",
            ["Tentacle__EnvironmentIds"] = "1,2",
            ["Tentacle__HealthCheckPort"] = healthPort.ToString(),
            ["Tentacle__CertsPath"] = sandbox.CertsPath,
            ["Tentacle__WorkspacePath"] = sandbox.WorkspacePath,
            ["Kubernetes__UseScriptPods"] = "false",
            ["Kubernetes__Namespace"] = "test-namespace"
        });

        var requestBody = await registrationServer.WaitForFirstRegistrationAsync(TestCancellationToken);

        var started = await process.WaitForOutputContainsAsync(
            "Squid Tentacle running",
            TimeSpan.FromSeconds(20),
            TestCancellationToken);

        started.ShouldBeTrue($"Tentacle did not report running state. Output:{Environment.NewLine}{process.CombinedOutput}");
        registrationServer.LastAuthorizationHeader.ShouldBe("Bearer unit-test-token");

        using (var doc = JsonDocument.Parse(requestBody))
        {
            var root = doc.RootElement;
            root.GetProperty("machineName").GetString().ShouldBe(machineName);
            root.GetProperty("roles").GetString().ShouldBe("web,api");
            root.GetProperty("environmentIds").GetString().ShouldBe("1,2");
            root.GetProperty("namespace").GetString().ShouldBe("test-namespace");

            var subscriptionId = root.GetProperty("subscriptionId").GetString();
            subscriptionId.ShouldNotBeNullOrWhiteSpace();
            process.CombinedOutput.ShouldContain("Halibut polling started");
            process.CombinedOutput.ShouldContain(subscriptionId!);
        }

        var ready = await WaitForReadyAsync(healthPort, TestCancellationToken);
        ready.ShouldBeTrue($"Readiness endpoint did not return 200. Output:{Environment.NewLine}{process.CombinedOutput}");
    }

    private sealed class TemporaryTentacleDirectory : IDisposable
    {
        private readonly string _root;

        public TemporaryTentacleDirectory()
        {
            _root = Path.Combine(Path.GetTempPath(), "squid-tentacle-tests", Guid.NewGuid().ToString("N"));
            CertsPath = Path.Combine(_root, "certs");
            WorkspacePath = Path.Combine(_root, "work");

            Directory.CreateDirectory(CertsPath);
            Directory.CreateDirectory(WorkspacePath);
        }

        public string CertsPath { get; }
        public string WorkspacePath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort cleanup on CI/local runs.
            }
        }
    }
}
