using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using k8s.Models;
using Squid.Agent.Configuration;
using Squid.Agent.Kubernetes;
using Squid.Agent.ScriptExecution;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.Agent;

public class ScriptPodServiceTests : IDisposable
{
    private readonly AgentSettings _settings;
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly string _tempWorkspace;

    public ScriptPodServiceTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        _settings = new AgentSettings
        {
            AgentNamespace = "test-ns",
            WorkspacePath = _tempWorkspace,
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc"
        };

        _ops = new Mock<IKubernetesPodOperations>();

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);
    }

    [Fact]
    public void StartScript_CreatesWorkspaceDirectory()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        var ticket = service.StartScript(command);

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_WritesScriptFile()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello world");

        var ticket = service.StartScript(command);

        var scriptPath = Path.Combine(_tempWorkspace, ticket.TaskId, "script.sh");
        File.Exists(scriptPath).ShouldBeTrue();
        File.ReadAllText(scriptPath).ShouldBe("echo hello world");
    }

    [Fact]
    public void StartScript_CallsCreatePod()
    {
        var service = CreateService();
        var command = MakeCommand("echo test");

        service.StartScript(command);

        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void StartScript_ReturnsUniqueTicket()
    {
        var service = CreateService();

        var ticket1 = service.StartScript(MakeCommand("echo 1"));
        var ticket2 = service.StartScript(MakeCommand("echo 2"));

        ticket1.TaskId.ShouldNotBe(ticket2.TaskId);
    }

    [Theory]
    [InlineData("Succeeded", ProcessState.Complete)]
    [InlineData("Failed", ProcessState.Complete)]
    [InlineData("Running", ProcessState.Running)]
    [InlineData("Pending", ProcessState.Running)]
    [InlineData(null, ProcessState.Complete)]
    public void GetStatus_MapsPodPhaseToProcessState(string phase, ProcessState expectedState)
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase(phase);
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(expectedState);
    }

    [Fact]
    public void GetStatus_UnknownTicket_ReturnsCompletedWithMinusOne()
    {
        var service = CreateService();

        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public void CompleteScript_DeletesPod()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo done"));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("done");

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void CompleteScript_CleansUpWorkspace()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo done"));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeTrue();

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        Directory.Exists(workDir).ShouldBeFalse();
    }

    [Fact]
    public void CancelScript_DeletesPodAndReturnsMinusOne()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("sleep 999"));

        var status = service.CancelScript(new CancelScriptCommand(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(-1);

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void CancelScript_UnknownTicket_ReturnsCompletedWithMinusOne()
    {
        var service = CreateService();

        var status = service.CancelScript(new CancelScriptCommand(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(-1);
    }

    // ========== Helpers ==========

    private ScriptPodService CreateService()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _settings);
        return new ScriptPodService(_settings, podManager);
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null);
    }

    private void SetupPodPhase(string phase)
    {
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus { Phase = phase }
            });
    }

    private void SetupPodExitCode(int exitCode)
    {
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus
                {
                    Phase = "Succeeded",
                    ContainerStatuses = new List<V1ContainerStatus>
                    {
                        new()
                        {
                            Name = "script",
                            State = new V1ContainerState
                            {
                                Terminated = new V1ContainerStateTerminated { ExitCode = exitCode }
                            }
                        }
                    }
                }
            });
    }

    private void SetupPodLogs(string logs)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs));

        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stream);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
                Directory.Delete(_tempWorkspace, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
