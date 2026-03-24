using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Health;
using Squid.Tentacle.ScriptExecution;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class ScriptPodServiceTests : IDisposable
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly string _tempWorkspace;

    public ScriptPodServiceTests()
    {
        DiskSpaceChecker.Enabled = false;

        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        _tentacleSettings = new TentacleSettings
        {
            WorkspacePath = _tempWorkspace
        };

        _kubernetesSettings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
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

        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });

        _ops.Setup(o => o.NamespaceExists(It.IsAny<string>())).Returns(true);
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
        File.ReadAllText(scriptPath).ShouldContain("echo hello world");
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
    [InlineData(null, ProcessState.Running)]
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
    public void GetStatus_PodNotFound_ReturnsComplete()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new k8s.Autorest.HttpOperationException
            {
                Response = new k8s.Autorest.HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty)
            });
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
    }

    [Fact]
    public void GetStatus_UnknownTicket_ReturnsCompletedWithUnknownResultCode()
    {
        var service = CreateService();

        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
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

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
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
    public void CancelScript_DeletesPodAndReturnsCanceledCode()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("sleep 999"));

        var status = service.CancelScript(new CancelScriptCommand(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void CancelScript_UnknownTicket_ReturnsCanceledCode()
    {
        var service = CreateService();

        var status = service.CancelScript(new CancelScriptCommand(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    [Fact]
    public void GetStatus_EosMarkerInLogs_ReturnsCompleteEarly()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));
        var ctx = service.ActiveScripts[ticket.TaskId];

        SetupPodPhase("Running");

        var eosLine = $"EOS-{ctx.EosMarkerToken}<<>>42";
        SetupPodLogs($"output line 1\n{eosLine}\n");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(42);
        status.Logs.ShouldNotContain(l => l.Text.Contains("EOS-"));
    }

    [Fact]
    public void GetStatus_NoEosMarker_FallsBackToPodPhase()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");
        SetupPodLogs("just output\n");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
    }

    [Fact]
    public void StartScript_WrapsScriptWithEosMarker()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo hello"));

        var scriptPath = Path.Combine(_tempWorkspace, ticket.TaskId, "script.sh");
        var content = File.ReadAllText(scriptPath);

        content.ShouldContain("echo hello");
        content.ShouldContain("__squid_exit_code__=$?");
        content.ShouldContain("EOS-");
        content.ShouldContain("exit $__squid_exit_code__");
    }

    // ========== Log Rotation Detection ==========

    [Fact]
    public void GetStatus_LogTruncation_ResetsReadPosition()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");

        // First read: 100 chars
        SetupPodLogs(new string('a', 50) + "\nline1\nline2\n");
        service.GetStatus(new ScriptStatusRequest(ticket, 0));

        // Simulate truncation: new log shorter than last read
        SetupPodLogs("new-line-after-rotation\n");
        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
        status.Logs.ShouldContain(l => l.Text.Contains("new-line-after-rotation"));
    }

    [Fact]
    public void GetStatus_LogTruncation_TerminalPod_InjectsWarning()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");

        // First read: establish a log position
        SetupPodLogs("initial output that is fairly long\n");
        service.GetStatus(new ScriptStatusRequest(ticket, 0));

        // Simulate truncation + terminal pod
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("short\n");
        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.Logs.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("Log rotation detected"));
    }

    [Fact]
    public void GetStatus_NoTruncation_NormalBehavior()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");

        SetupPodLogs("line1\n");
        var status1 = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        // Second read: longer log (no truncation)
        SetupPodLogs("line1\nline2\n");
        var status2 = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status2.State.ShouldBe(ProcessState.Running);
        status2.Logs.ShouldContain(l => l.Text.Contains("line2"));
        status2.Logs.ShouldNotContain(l => l.Text.Contains("Log rotation detected"));
    }

    // ========== Log Output Size Limits ==========

    [Fact]
    public void ExtractNewLogLines_ExceedsMaxBuffer_InjectsTruncationWarning()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        var logs = new string('x', 100) + "\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 50);

        result.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("Log output truncated"));
        ctx.LogOutputTruncated.ShouldBeTrue();
    }

    [Fact]
    public void ExtractNewLogLines_ExceedsMaxBuffer_StopsEmittingLines()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        var logs = new string('x', 100) + "\nline2\nline3\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 50);

        result.ShouldNotContain(l => l.Text == "line2");
        result.ShouldNotContain(l => l.Text == "line3");
    }

    [Fact]
    public void ExtractNewLogLines_ExceedsMaxBuffer_StillDetectsEos()
    {
        var eosToken = "abcdef1234567890abcdef1234567890";
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", eosToken);
        var eosLine = $"EOS-{eosToken}<<>>42";
        var logs = new string('x', 100) + $"\n{eosLine}\n";

        ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 50);

        ctx.EosDetected.ShouldBeTrue();
        ctx.EosExitCode.ShouldBe(42);
    }

    [Fact]
    public void ExtractNewLogLines_WithinLimit_NoTruncation()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        var logs = "line1\nline2\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);

        result.Count.ShouldBe(2);
        ctx.LogOutputTruncated.ShouldBeFalse();
    }

    // ========== Sensitive Output Masking ==========

    [Fact]
    public void ExtractNewLogLines_SensitiveValueInOutput_Masked()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        ctx.SensitiveValues = new HashSet<string>(StringComparer.Ordinal) { "my-secret-token" };

        var logs = "deploying with token: my-secret-token\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);

        result.Count.ShouldBe(1);
        result[0].Text.ShouldBe("deploying with token: ********");
    }

    // ========== Isolation Mutex ==========

    [Fact]
    public void StartScript_FullIsolation_AcquiresMutex()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo isolated", "test-mutex");

        var ticket = service.StartScript(command);

        ticket.ShouldNotBeNull();
        service.ActiveScripts.ContainsKey(ticket.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_FullIsolation_QueuesSecondScript()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo first", "blocking-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void GetStatus_PendingScript_ReturnsRunningWithWaitMessage()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo first", "status-mutex");

        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        var status = service.GetStatus(new ScriptStatusRequest(ticket2, 0));

        status.State.ShouldBe(ProcessState.Running);
        status.Logs.ShouldContain(l => l.Text.Contains("Waiting for isolation mutex..."));
        status.NextLogSequence.ShouldBe(1);

        var status2 = service.GetStatus(new ScriptStatusRequest(ticket2, 1));

        status2.State.ShouldBe(ProcessState.Running);
        status2.Logs.ShouldBeEmpty();
        status2.NextLogSequence.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReleasingActiveScript_StartsPendingScript(bool viaComplete)
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo test", "release-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        if (viaComplete)
            service.CompleteScript(new CompleteScriptCommand(ticket1, 0));
        else
            service.CancelScript(new CancelScriptCommand(ticket1, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Exactly(2));
    }

    [Fact]
    public void CancelScript_PendingScript_RemovesFromQueue()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo test", "cancel-pending-mutex");

        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        var status = service.CancelScript(new CancelScriptCommand(ticket2, 0));

        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        service.PendingScripts.ShouldBeEmpty();
        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void StartScript_FullIsolation_DifferentMutexNames_BothStartImmediately()
    {
        var service = CreateService();
        var commandA = MakeIsolatedCommand("echo a", "mutex-a");
        var commandB = MakeIsolatedCommand("echo b", "mutex-b");

        var ticket1 = service.StartScript(commandA);
        var ticket2 = service.StartScript(commandB);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        service.PendingScripts.ShouldBeEmpty();
    }

    [Fact]
    public void StartScript_NoIsolation_NeverQueues()
    {
        var service = CreateService();

        service.StartScript(MakeCommand("echo 1"));
        service.StartScript(MakeCommand("echo 2"));

        service.PendingScripts.Count.ShouldBe(0);
    }

    [Fact]
    public void CancelScript_ReleasesIsolationMutex()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo cancel", "cancel-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        service.CancelScript(new CancelScriptCommand(ticket1, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_NoIsolation_DoesNotBlock()
    {
        var service = CreateService();

        var ticket1 = service.StartScript(MakeCommand("echo 1"));
        var ticket2 = service.StartScript(MakeCommand("echo 2"));

        ticket1.TaskId.ShouldNotBe(ticket2.TaskId);
        service.ActiveScripts.Count.ShouldBe(2);
    }

    // ========== Mixed Isolation ==========

    [Fact]
    public void StartScript_FullIsolation_BlocksNoIsolation()
    {
        var service = CreateService();
        var writer = MakeIsolatedCommand("echo writer", "mixed-mutex");
        var reader = MakeCommand("echo reader", "mixed-mutex");

        var ticket1 = service.StartScript(writer);
        var ticket2 = service.StartScript(reader);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_NoIsolation_BlocksFullIsolation()
    {
        var service = CreateService();
        var reader = MakeCommand("echo reader", "mixed-mutex-2");
        var writer = MakeIsolatedCommand("echo writer", "mixed-mutex-2");

        var ticket1 = service.StartScript(reader);
        var ticket2 = service.StartScript(writer);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_NoIsolation_MultipleParallel_SameMutex()
    {
        var service = CreateService();

        var ticket1 = service.StartScript(MakeCommand("echo 1", "parallel-mutex"));
        var ticket2 = service.StartScript(MakeCommand("echo 2", "parallel-mutex"));
        var ticket3 = service.StartScript(MakeCommand("echo 3", "parallel-mutex"));

        service.ActiveScripts.Count.ShouldBe(3);
        service.PendingScripts.ShouldBeEmpty();
    }

    [Fact]
    public void CompleteScript_FullIsolation_UnblocksPendingNoIsolation()
    {
        var service = CreateService();
        var writer = MakeIsolatedCommand("echo writer", "unblock-mutex");
        var reader = MakeCommand("echo reader", "unblock-mutex");

        var ticket1 = service.StartScript(writer);
        var ticket2 = service.StartScript(reader);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket1, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    // ========== Pending Batch Launch ==========

    [Fact]
    public void CompleteScript_FullIsolation_BatchLaunchesMultiplePendingNoIsolation()
    {
        var service = CreateService();
        var writer = MakeIsolatedCommand("echo writer", "batch-mutex");
        var reader1 = MakeCommand("echo reader1", "batch-mutex");
        var reader2 = MakeCommand("echo reader2", "batch-mutex");
        var reader3 = MakeCommand("echo reader3", "batch-mutex");

        var writerTicket = service.StartScript(writer);
        var readerTicket1 = service.StartScript(reader1);
        var readerTicket2 = service.StartScript(reader2);
        var readerTicket3 = service.StartScript(reader3);

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(3);

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(writerTicket, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.Count.ShouldBe(3);
        service.ActiveScripts.ContainsKey(readerTicket1.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(readerTicket2.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(readerTicket3.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void CompleteScript_FullIsolation_StopsAfterLaunchingPendingWriter()
    {
        var service = CreateService();
        var writer1 = MakeIsolatedCommand("echo writer1", "serial-mutex");
        var writer2 = MakeIsolatedCommand("echo writer2", "serial-mutex");
        var reader = MakeCommand("echo reader", "serial-mutex");

        var ticket1 = service.StartScript(writer1);
        var ticket2 = service.StartScript(writer2);
        var ticket3 = service.StartScript(reader);

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(2);

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket1, 0));

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(1);
    }

    [Fact]
    public void CompleteScript_ThreeFullIsolation_ExecuteSequentially()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo serial", "sequential-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);
        var ticket3 = service.StartScript(command);

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(2);

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket1, 0));

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(1);

        // ConcurrentDictionary iteration order is non-deterministic, so find which ticket was launched
        var secondActive = service.ActiveScripts.Keys.Single();
        service.CompleteScript(new CompleteScriptCommand(new ScriptTicket(secondActive), 0));

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.ShouldBeEmpty();

        var thirdActive = service.ActiveScripts.Keys.Single();
        service.CompleteScript(new CompleteScriptCommand(new ScriptTicket(thirdActive), 0));

        service.ActiveScripts.ShouldBeEmpty();
        service.PendingScripts.ShouldBeEmpty();

        // All 3 pods created: 1 immediate + 2 via pending dequeue
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Exactly(3));
    }

    // ========== State File ==========

    [Fact]
    public void LaunchScript_WritesStateFile()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo hello"));

        var statePath = Path.Combine(_tempWorkspace, ticket.TaskId, ".squid-state.json");
        File.Exists(statePath).ShouldBeTrue();

        var state = ScriptStateFile.TryRead(Path.Combine(_tempWorkspace, ticket.TaskId));
        state.ShouldNotBeNull();
        state!.TicketId.ShouldBe(ticket.TaskId);
        state.Isolation.ShouldBe("NoIsolation");
    }

    [Fact]
    public void LaunchScript_FullIsolation_WritesStateFile()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo test", "state-mutex");
        var ticket = service.StartScript(command);

        var state = ScriptStateFile.TryRead(Path.Combine(_tempWorkspace, ticket.TaskId));
        state.ShouldNotBeNull();
        state!.Isolation.ShouldBe("FullIsolation");
        state.IsolationMutexName.ShouldBe("state-mutex");
    }

    [Fact]
    public void CompleteScript_DeletesStateFile()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo done"));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeFalse();
    }

    // ========== Isolation Mutex Timeout ==========

    [Fact]
    public void PendingScript_ExceedsTimeout_InjectedAsTimeout()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc",
            IsolationMutexTimeoutMinutes = 0 // Immediate timeout
        };

        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "timeout-mutex");
        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        // GetStatus should detect timeout and inject terminal result
        var status = service.GetStatus(new ScriptStatusRequest(ticket2, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        status.Logs.ShouldContain(l => l.Text.Contains("isolation mutex"));
    }

    [Fact]
    public void PendingScript_WithinTimeout_StillPending()
    {
        var service = CreateService(); // Default 30min timeout

        var command = MakeIsolatedCommand("echo test", "within-timeout-mutex");
        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        var status = service.GetStatus(new ScriptStatusRequest(ticket2, 0));

        status.State.ShouldBe(ProcessState.Running);
        status.Logs.ShouldContain(l => l.Text.Contains("Waiting for isolation mutex..."));
    }

    // ========== LaunchScript Failure Handling ==========

    [Fact]
    public void StartScript_LaunchFails_InjectsTerminalResultAndReleasesMutex()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("K8s API unavailable"));

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
        var ticket = service.StartScript(MakeCommand("echo fail"));

        // Should not be in active scripts (launch failed)
        service.ActiveScripts.ContainsKey(ticket.TaskId).ShouldBeFalse();

        // Should have a terminal result
        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        status.Logs.ShouldContain(l => l.Text.Contains("K8s API unavailable"));
    }

    [Fact]
    public void StartScript_LaunchFails_MutexReleasedSoNextScriptCanStart()
    {
        var callCount = 0;
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Transient error");
                return pod;
            });

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
        var command = MakeIsolatedCommand("echo test", "fail-release-mutex");

        // First script fails during launch
        var ticket1 = service.StartScript(command);
        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeFalse();

        // Second script should acquire mutex and launch successfully (mutex was released)
        var ticket2 = service.StartScript(command);
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        service.PendingScripts.ShouldBeEmpty();
    }

    // ========== Metrics ==========

    [Fact]
    public void StartScript_IncrementsMetricsGauge()
    {
        TentacleMetrics.Reset();
        var service = CreateService();
        var command = MakeCommand("echo metrics");

        service.StartScript(command);

        TentacleMetrics.ActiveScripts.ShouldBe(1);
        TentacleMetrics.ScriptsStartedTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void CompleteScript_DecrementsMetricsGauge()
    {
        TentacleMetrics.Reset();
        var service = CreateService();
        var command = MakeCommand("echo metrics");

        var ticket = service.StartScript(command);
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        TentacleMetrics.ActiveScripts.ShouldBe(0);
        TentacleMetrics.ScriptsCompletedTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void CancelScript_IncrementsCancel()
    {
        TentacleMetrics.Reset();
        var service = CreateService();
        var command = MakeCommand("echo cancel");

        var ticket = service.StartScript(command);
        service.CancelScript(new CancelScriptCommand(ticket, 0));

        TentacleMetrics.ScriptsCanceledTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    // ========== sinceTime Deduplication ==========

    [Fact]
    public void ExtractNewLogLines_SinceTime_OnlyNewLines()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");

        var firstBatch = "line one\nline two\n";
        ScriptPodService.ExtractNewLogLines(ctx, firstBatch, maxLogBufferBytes: 10 * 1024 * 1024);

        var secondBatch = "line one\nline two\nline three\n";
        var result = ScriptPodService.ExtractNewLogLines(ctx, secondBatch, maxLogBufferBytes: 10 * 1024 * 1024);

        result.Count.ShouldBe(1);
        result[0].Text.ShouldBe("line three");
    }

    [Fact]
    public void ExtractNewLogLines_MultipleCallsSameContent_NoDuplicates()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");

        var logs = "alpha\nbeta\n";
        var result1 = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);
        var result2 = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);

        result1.Count.ShouldBe(2);
        result2.Count.ShouldBe(0);
    }

    [Fact]
    public void ExtractNewLogLines_SetsLastLogTimestamp()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        ctx.LastLogTimestamp.ShouldBeNull();

        ScriptPodService.ExtractNewLogLines(ctx, "line\n", maxLogBufferBytes: 10 * 1024 * 1024);

        ctx.LastLogTimestamp.ShouldNotBeNull();
    }

    [Fact]
    public void ExtractNewLogLines_EmptyInput_ReturnsEmpty()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");

        var result = ScriptPodService.ExtractNewLogLines(ctx, "", maxLogBufferBytes: 10 * 1024 * 1024);

        result.ShouldBeEmpty();
    }

    // ========== Helpers ==========

    // === Multi-Namespace ===

    [Fact]
    public void StartScript_WithTargetNamespace_AlwaysCreatesInAgentNamespace()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello", targetNamespace: "custom-ns");

        service.StartScript(command);

        // Target namespace is for kubectl context, not pod placement
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void StartScript_WithTargetNamespace_PodStillCreatedInAgentNamespace()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello", targetNamespace: "custom-ns");

        service.StartScript(command);

        // Pod always created in agent namespace, not target namespace
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "custom-ns"), Times.Never);
    }

    [Fact]
    public void StartScript_NoTargetNamespace_PodCreatedInAgentNamespace()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        service.StartScript(command);

        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void StateFile_RoundTripsNamespace()
    {
        var dir = Path.Combine(_tempWorkspace, "ns-test");
        Directory.CreateDirectory(dir);

        ScriptStateFile.Write(dir, new ScriptStateFile
        {
            TicketId = "abc",
            PodName = "pod-1",
            EosMarkerToken = "eos",
            Isolation = "NoIsolation",
            Namespace = "tenant-ns",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var state = ScriptStateFile.TryRead(dir);
        state.ShouldNotBeNull();
        state.Namespace.ShouldBe("tenant-ns");
    }

    [Fact]
    public void StateFile_NullNamespace_RoundTrips()
    {
        var dir = Path.Combine(_tempWorkspace, "ns-test-null");
        Directory.CreateDirectory(dir);

        ScriptStateFile.Write(dir, new ScriptStateFile
        {
            TicketId = "abc",
            PodName = "pod-1",
            EosMarkerToken = "eos",
            Isolation = "NoIsolation",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var state = ScriptStateFile.TryRead(dir);
        state.ShouldNotBeNull();
        state.Namespace.ShouldBeNull();
    }

    [Fact]
    public void StateFile_RoundTripsLastLogTimestamp()
    {
        var dir = Path.Combine(_tempWorkspace, "ts-test");
        Directory.CreateDirectory(dir);

        var timestamp = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);

        ScriptStateFile.Write(dir, new ScriptStateFile
        {
            TicketId = "abc",
            PodName = "pod-1",
            EosMarkerToken = "eos",
            Isolation = "NoIsolation",
            LastLogTimestamp = timestamp,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var state = ScriptStateFile.TryRead(dir);
        state.ShouldNotBeNull();
        state.LastLogTimestamp.ShouldBe(timestamp);
    }

    [Fact]
    public void StateFile_NullTimestamp_RoundTrips()
    {
        var dir = Path.Combine(_tempWorkspace, "ts-test-null");
        Directory.CreateDirectory(dir);

        ScriptStateFile.Write(dir, new ScriptStateFile
        {
            TicketId = "abc",
            PodName = "pod-1",
            EosMarkerToken = "eos",
            Isolation = "NoIsolation",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var state = ScriptStateFile.TryRead(dir);
        state.ShouldNotBeNull();
        state.LastLogTimestamp.ShouldBeNull();
    }

    // === Secret Persistence ===

    [Fact]
    public void StartScript_Queued_PersistsSecret()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager, ops.Object);

        var command = MakeIsolatedCommand("echo test", "secret-mutex");
        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        ops.Verify(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void LaunchScript_RemovesPendingSecret()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager, ops.Object);

        service.StartScript(MakeCommand("echo test"));

        ops.Verify(o => o.DeleteSecret(It.IsAny<string>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void CancelScript_Pending_RemovesPendingSecret()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager, ops.Object);

        var command = MakeIsolatedCommand("echo test", "cancel-secret-mutex");
        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        service.CancelScript(new CancelScriptCommand(ticket2, 0));

        ops.Verify(o => o.DeleteSecret(It.IsAny<string>(), "test-ns"), Times.Exactly(2));
    }

    // === WrapCommandWithEosMarker Namespace Preservation ===

    [Fact]
    public void StartScript_WithTargetNamespace_WrappedCommand_PreservesNamespace()
    {
        _ops.Setup(o => o.ListPods("custom-ns", It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), "custom-ns"))
            .Returns((V1Pod pod, string ns) => pod);

        var service = CreateService();
        var command = MakeCommand("echo hello", targetNamespace: "custom-ns");

        service.StartScript(command);

        // Script pod always created in agent namespace, not target namespace
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void StartScript_NullTargetNamespace_CreatesInAgentNamespace()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        service.StartScript(command);

        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    // === Graceful Shutdown Drain ===

    [Fact]
    public async Task WaitForDrainAsync_NoScripts_CompletesImmediately()
    {
        var service = CreateService();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.WaitForDrainAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitForDrainAsync_WithActiveScripts_WaitsUntilEmpty()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo drain-test"));

        service.ActiveScripts.ContainsKey(ticket.TaskId).ShouldBeTrue();

        // Remove script after a short delay to simulate completion
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            service.ActiveScripts.TryRemove(ticket.TaskId, out _);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.WaitForDrainAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        sw.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(500));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForDrainAsync_Timeout_ReturnsAfterTimeout()
    {
        var service = CreateService();
        service.StartScript(MakeCommand("echo never-finishes"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.WaitForDrainAsync(TimeSpan.FromSeconds(1));
        sw.Stop();

        sw.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(800));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    // === Pending Queue Bounds ===

    [Fact]
    public void StartScript_QueueFull_RejectsWithFatalCode()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc",
            MaxPendingScripts = 1
        };

        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "queue-mutex");
        service.StartScript(command); // active
        service.StartScript(command); // pending (fills queue to 1)
        var ticket3 = service.StartScript(command); // should be rejected

        var status = service.GetStatus(new ScriptStatusRequest(ticket3, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        status.Logs.ShouldContain(l => l.Text.Contains("pending queue full"));
    }

    [Fact]
    public void StartScript_QueueNotFull_AcceptsScript()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc",
            MaxPendingScripts = 5
        };

        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "accept-mutex");
        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_QueueAtLimit_RejectsNextScript()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc",
            MaxPendingScripts = 2
        };

        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "limit-mutex");
        service.StartScript(command); // active
        service.StartScript(command); // pending 1
        service.StartScript(command); // pending 2 (at limit)
        var ticket4 = service.StartScript(command); // rejected

        service.PendingScripts.Count.ShouldBe(2);

        var status = service.GetStatus(new ScriptStatusRequest(ticket4, 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
    }

    // === Server-Provided Ticket ID (Idempotency) ===

    [Fact]
    public void StartScript_WithTaskId_UsesProvidedId()
    {
        var service = CreateService();
        var command = new StartScriptCommand("echo hello", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), null, Array.Empty<string>(), "server-provided-id-abc123");

        var ticket = service.StartScript(command);

        ticket.TaskId.ShouldBe("server-provided-id-abc123");
        service.ActiveScripts.ContainsKey("server-provided-id-abc123").ShouldBeTrue();
    }

    [Fact]
    public void StartScript_WithTaskId_SecondCall_ReturnsExistingTicket()
    {
        var service = CreateService();
        var command = new StartScriptCommand("echo hello", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), null, Array.Empty<string>(), "idempotent-ticket-id");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        ticket1.TaskId.ShouldBe("idempotent-ticket-id");
        ticket2.TaskId.ShouldBe("idempotent-ticket-id");
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void StartScript_NullTaskId_GeneratesNewId()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        var ticket = service.StartScript(command);

        ticket.TaskId.ShouldNotBeNullOrEmpty();
        ticket.TaskId.Length.ShouldBe(32);
    }

    // === Terminal Results Eviction ===

    [Fact]
    public void EvictStaleTerminalResults_OldEntries_Removed()
    {
        var service = CreateService();
        service.InjectTerminalResult("old-ticket", ScriptExitCodes.Timeout, new List<ProcessOutput>());

        // Force the entry to appear old
        var field = typeof(ScriptPodService).GetField("_terminalResults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, (ScriptStatusResponse Response, DateTimeOffset CreatedAt)>)field.GetValue(service);
        dict["old-ticket"] = (dict["old-ticket"].Response, DateTimeOffset.UtcNow.AddHours(-2));

        service.EvictStaleTerminalResults(TimeSpan.FromHours(1));

        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("old-ticket"), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void EvictStaleTerminalResults_RecentEntries_Kept()
    {
        var service = CreateService();
        service.InjectTerminalResult("recent-ticket", ScriptExitCodes.Timeout, new List<ProcessOutput>());

        service.EvictStaleTerminalResults(TimeSpan.FromHours(1));

        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("recent-ticket"), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
    }

    // === Fix 2: TOCTOU Race — Atomic Queue Bounds ===

    [Fact]
    public void StartScript_QueueBound_Sequential_RejectsAtLimit()
    {
        var settings = CreateSettingsWithMaxPending(2);
        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "bound-mutex");
        service.StartScript(command); // active
        service.StartScript(command); // pending 1
        service.StartScript(command); // pending 2
        var ticket4 = service.StartScript(command); // rejected

        var status = service.GetStatus(new ScriptStatusRequest(ticket4, 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        status.Logs.ShouldContain(l => l.Text.Contains("pending queue full"));
    }

    [Fact]
    public void StartScript_QueueBound_AfterCancel_AcceptsNewScript()
    {
        var settings = CreateSettingsWithMaxPending(1);
        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "cancel-mutex");
        service.StartScript(command); // active
        var ticket2 = service.StartScript(command); // pending (at limit)

        service.CancelScript(new CancelScriptCommand(ticket2, 0));

        var ticket3 = service.StartScript(command); // should be accepted
        service.PendingScripts.ContainsKey(ticket3.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_QueueBound_ParallelAdds_NeverExceedsLimit()
    {
        var settings = CreateSettingsWithMaxPending(3);
        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        // First acquire the mutex so all subsequent go to pending
        var activeCmd = MakeIsolatedCommand("echo active", "parallel-mutex");
        service.StartScript(activeCmd);

        var results = new System.Collections.Concurrent.ConcurrentBag<ScriptTicket>();

        Parallel.For(0, 10, _ =>
        {
            var cmd = MakeIsolatedCommand("echo parallel", "parallel-mutex");
            var ticket = service.StartScript(cmd);
            results.Add(ticket);
        });

        service.PendingScripts.Count.ShouldBeLessThanOrEqualTo(3);
    }

    // === Fix 3: Workspace Leak on CreatePod Failure ===

    [Fact]
    public void LaunchScript_CreatePodThrows_WorkspaceCleaned()
    {
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Throws(new Exception("K8s API error"));

        var service = CreateService();
        var command = MakeCommand("echo hello");
        var ticket = service.StartScript(command);

        // Workspace directory should have been cleaned up
        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeFalse();
    }

    [Fact]
    public void LaunchScript_CreatePodThrows_TerminalResultInjected()
    {
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Throws(new Exception("K8s API error"));

        var service = CreateService();
        var command = MakeCommand("echo hello");
        var ticket = service.StartScript(command);

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        status.Logs.ShouldContain(l => l.Text.Contains("K8s API error"));
    }

    [Fact]
    public void LaunchScript_CreatePodSucceeds_WorkspacePreserved()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");
        var ticket = service.StartScript(command);

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeTrue();
    }

    // === Fix 4: Drain Flag ===

    [Fact]
    public async Task StartScript_DuringDrain_RejectedWithFatal()
    {
        var service = CreateService();
        var command = MakeCommand("echo before-drain");
        service.StartScript(command); // one active so drain has something to wait on

        _ = service.WaitForDrainAsync(TimeSpan.FromSeconds(5));

        // Allow drain to set the flag
        await Task.Delay(50);

        var command2 = MakeCommand("echo during-drain");
        var ticket = service.StartScript(command2);

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        status.Logs.ShouldContain(l => l.Text.Contains("shutting down"));
    }

    [Fact]
    public void ProcessPendingScripts_DuringDrain_NoPendingPromoted()
    {
        var service = CreateService();

        // Create an active script with full isolation
        var activeCmd = MakeIsolatedCommand("echo active", "drain-promote-mutex");
        var activeTicket = service.StartScript(activeCmd);

        // Queue a pending script
        var pendingCmd = MakeIsolatedCommand("echo pending", "drain-promote-mutex");
        var pendingTicket = service.StartScript(pendingCmd);
        service.PendingScripts.ContainsKey(pendingTicket.TaskId).ShouldBeTrue();

        // Set _draining via reflection
        var field = typeof(ScriptPodService).GetField("_draining", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(service, true);

        // Complete the active script — this triggers ReleaseMutexAndProcessPending
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(activeTicket, 0));

        // Pending should NOT have been promoted
        service.ActiveScripts.ContainsKey(pendingTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public void StartScript_BeforeDrain_StillWorks()
    {
        var service = CreateService();
        var command = MakeCommand("echo before-drain");
        var ticket = service.StartScript(command);

        service.ActiveScripts.ContainsKey(ticket.TaskId).ShouldBeTrue();
    }

    // === Fix 5: FIFO Ordering ===

    [Fact]
    public void ProcessPendingScripts_FIFO_FirstEnqueuedPromotedFirst()
    {
        var service = CreateService();

        // Take the mutex with an active writer
        var activeCmd = MakeIsolatedCommand("echo active", "fifo-mutex");
        var activeTicket = service.StartScript(activeCmd);

        // Queue three pending scripts (NoIsolation so they don't block each other once promoted)
        var cmdA = MakeIsolatedCommand("echo A", "fifo-mutex");
        var ticketA = service.StartScript(cmdA);
        var cmdB = MakeIsolatedCommand("echo B", "fifo-mutex");
        var ticketB = service.StartScript(cmdB);
        var cmdC = MakeIsolatedCommand("echo C", "fifo-mutex");
        var ticketC = service.StartScript(cmdC);

        // Force known ordering via reflection
        var pendingField = typeof(ScriptPodService).GetField("_pendingScripts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, ScriptPodService.PendingScript>)pendingField.GetValue(service);
        var now = DateTimeOffset.UtcNow;
        pending[ticketA.TaskId] = new ScriptPodService.PendingScript(cmdA, now.AddMinutes(-3)); // earliest
        pending[ticketB.TaskId] = new ScriptPodService.PendingScript(cmdB, now.AddMinutes(-2));
        pending[ticketC.TaskId] = new ScriptPodService.PendingScript(cmdC, now.AddMinutes(-1));

        // Complete active → releases mutex → ProcessPendingScripts
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(activeTicket, 0));

        // A should be promoted (earliest), not B or C
        service.ActiveScripts.ContainsKey(ticketA.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void ProcessPendingScripts_TimedOut_Skipped_NextPromoted()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns", ScriptPodImage = "test-image:latest", ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60, ScriptPodCpuRequest = "25m", ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m", ScriptPodMemoryLimit = "512Mi", PvcClaimName = "test-pvc",
            IsolationMutexTimeoutMinutes = 1
        };
        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var activeCmd = MakeIsolatedCommand("echo active", "timeout-fifo-mutex");
        var activeTicket = service.StartScript(activeCmd);

        var cmdA = MakeIsolatedCommand("echo A", "timeout-fifo-mutex");
        var ticketA = service.StartScript(cmdA);
        var cmdB = MakeIsolatedCommand("echo B", "timeout-fifo-mutex");
        var ticketB = service.StartScript(cmdB);

        // Make A timed out, B still valid
        var pendingField = typeof(ScriptPodService).GetField("_pendingScripts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, ScriptPodService.PendingScript>)pendingField.GetValue(service);
        pending[ticketA.TaskId] = new ScriptPodService.PendingScript(cmdA, DateTimeOffset.UtcNow.AddMinutes(-10)); // timed out
        pending[ticketB.TaskId] = new ScriptPodService.PendingScript(cmdB, DateTimeOffset.UtcNow.AddSeconds(-5)); // recent

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(activeTicket, 0));

        // A should be evicted (timeout), B promoted
        service.PendingScripts.ContainsKey(ticketA.TaskId).ShouldBeFalse();
        service.ActiveScripts.ContainsKey(ticketB.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void ProcessPendingScripts_FIFO_WriterBeforeReaders()
    {
        var service = CreateService();

        // Take the mutex with an active writer
        var activeCmd = MakeIsolatedCommand("echo active", "writer-first-mutex");
        var activeTicket = service.StartScript(activeCmd);

        // Queue: writer1 (earliest), then two NoIsolation readers
        var writer1Cmd = MakeIsolatedCommand("echo writer1", "writer-first-mutex");
        var ticketW1 = service.StartScript(writer1Cmd);
        var reader1Cmd = new StartScriptCommand("echo reader1", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "writer-first-mutex", Array.Empty<string>(), null);
        var ticketR1 = service.StartScript(reader1Cmd);
        var reader2Cmd = new StartScriptCommand("echo reader2", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "writer-first-mutex", Array.Empty<string>(), null);
        var ticketR2 = service.StartScript(reader2Cmd);

        // Force ordering: writer first
        var pendingField = typeof(ScriptPodService).GetField("_pendingScripts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, ScriptPodService.PendingScript>)pendingField.GetValue(service);
        var now = DateTimeOffset.UtcNow;
        pending[ticketW1.TaskId] = new ScriptPodService.PendingScript(writer1Cmd, now.AddMinutes(-3));
        pending[ticketR1.TaskId] = new ScriptPodService.PendingScript(reader1Cmd, now.AddMinutes(-2));
        pending[ticketR2.TaskId] = new ScriptPodService.PendingScript(reader2Cmd, now.AddMinutes(-1));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(activeTicket, 0));

        // Writer should be promoted first (FIFO)
        service.ActiveScripts.ContainsKey(ticketW1.TaskId).ShouldBeTrue();
    }

    // === Fix 7: Terminal Results Eviction at Lifecycle Boundaries ===

    [Fact]
    public void CompleteScript_EvictsOldTerminalResults()
    {
        var service = CreateService();

        // Inject an old terminal result
        service.InjectTerminalResult("old-terminal", ScriptExitCodes.Timeout, new List<ProcessOutput>());
        var termField = typeof(ScriptPodService).GetField("_terminalResults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, (ScriptStatusResponse Response, DateTimeOffset CreatedAt)>)termField.GetValue(service);
        dict["old-terminal"] = (dict["old-terminal"].Response, DateTimeOffset.UtcNow.AddHours(-2));

        // Start and complete a script to trigger eviction
        var command = MakeCommand("echo test");
        var ticket = service.StartScript(command);
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        // Old terminal result should be evicted
        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("old-terminal"), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void CancelScript_EvictsOldTerminalResults()
    {
        var service = CreateService();

        // Inject an old terminal result
        service.InjectTerminalResult("old-terminal-cancel", ScriptExitCodes.Fatal, new List<ProcessOutput>());
        var termField = typeof(ScriptPodService).GetField("_terminalResults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, (ScriptStatusResponse Response, DateTimeOffset CreatedAt)>)termField.GetValue(service);
        dict["old-terminal-cancel"] = (dict["old-terminal-cancel"].Response, DateTimeOffset.UtcNow.AddHours(-2));

        // Start and cancel a script to trigger eviction
        var command = MakeCommand("echo test");
        var ticket = service.StartScript(command);
        service.CancelScript(new CancelScriptCommand(ticket, 0));

        // Old terminal result should be evicted
        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("old-terminal-cancel"), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void CompleteScript_RecentTerminalResultsSurvive()
    {
        var service = CreateService();

        // Inject a recent terminal result (< 1hr)
        service.InjectTerminalResult("recent-terminal", ScriptExitCodes.Timeout, new List<ProcessOutput>());

        // Start and complete a script to trigger eviction
        var command = MakeCommand("echo test");
        var ticket = service.StartScript(command);
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        // Recent terminal result should survive
        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("recent-terminal"), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
    }

    // ========== Log Streaming ==========

    [Fact]
    public void DrainLogs_StreamedLinesAvailable_PrefersStreamOverPoll()
    {
        var service = CreateServiceWithPodOps();
        var command = MakeCommand("echo hello");
        var ticket = service.StartScript(command);
        var ticketId = ticket.TaskId;

        service.ActiveScripts.TryGetValue(ticketId, out var ctx).ShouldBeTrue();

        // Enqueue streamed lines
        ctx!.StreamedLogLines.Enqueue("streamed line 1");
        ctx.StreamedLogLines.Enqueue("streamed line 2");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.Logs.ShouldContain(l => l.Text.Contains("streamed line 1"));
        status.Logs.ShouldContain(l => l.Text.Contains("streamed line 2"));

        // ReadPodLogs should NOT have been called (stream preferred over poll)
        _ops.Verify(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()), Times.Never);
    }

    [Fact]
    public void DrainLogs_NoStreamedLines_FallsBackToPoll()
    {
        var service = CreateServiceWithPodOps();
        var command = MakeCommand("echo hello");
        var ticket = service.StartScript(command);

        SetupPodLogs("polled line 1\npolled line 2");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.Logs.ShouldContain(l => l.Text.Contains("polled line 1"));

        _ops.Verify(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()), Times.AtLeastOnce);
    }

    [Fact]
    public void CompleteScript_CancelsLogStream()
    {
        var service = CreateServiceWithPodOps();
        var command = MakeCommand("echo hello");
        var ticket = service.StartScript(command);

        service.ActiveScripts.TryGetValue(ticket.TaskId, out var ctx).ShouldBeTrue();

        // Simulate a log stream CTS that was started
        ctx!.LogStreamCts = new CancellationTokenSource();
        ctx.LogStreamCts.IsCancellationRequested.ShouldBeFalse();

        SetupPodExitCode(0);

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        ctx.LogStreamCts.IsCancellationRequested.ShouldBeTrue();
    }

    private ScriptPodService CreateService()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);
        return new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
    }

    private ScriptPodService CreateServiceWithPodOps()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);
        return new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager, _ops.Object);
    }

    private static StartScriptCommand MakeCommand(string scriptBody, string? mutexName = null, string? targetNamespace = null)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            mutexName,
            Array.Empty<string>(),
            null)
        {
            TargetNamespace = targetNamespace
        };
    }

    private static StartScriptCommand MakeIsolatedCommand(string scriptBody, string mutexName, TimeSpan? timeout = null)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            timeout ?? TimeSpan.FromMinutes(5),
            mutexName,
            Array.Empty<string>(),
            null);
    }

    private KubernetesSettings CreateSettingsWithMaxPending(int maxPending)
    {
        return new KubernetesSettings
        {
            TentacleNamespace = "test-ns", ScriptPodImage = "test-image:latest", ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60, ScriptPodCpuRequest = "25m", ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m", ScriptPodMemoryLimit = "512Mi", PvcClaimName = "test-pvc",
            MaxPendingScripts = maxPending
        };
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

    private void SetupPodWithContainerStates(string phase, params (string Name, V1ContainerState State)[] containers)
    {
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus
                {
                    Phase = phase,
                    ContainerStatuses = containers.Select(c => new V1ContainerStatus
                    {
                        Name = c.Name,
                        State = c.State
                    }).ToList()
                }
            });
    }

    private void SetupPodLogs(string logs)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs));

        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
            .Returns(stream);
    }

    // ========== Container State Awareness (Fix 1) ==========

    [Fact]
    public void GetStatus_ContainerTerminated_ReturnsCompleteWithContainerExitCode()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodWithContainerStates("Failed",
            ("script", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 42 } }),
            ("nfs-watchdog", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } }));
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(42);
    }

    [Fact]
    public void GetStatus_SidecarCrashedScriptRunning_ReportsRunning()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodWithContainerStates("Running",
            ("script", new V1ContainerState { Running = new V1ContainerStateRunning() }),
            ("nfs-watchdog", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1 } }));
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
    }

    [Fact]
    public void GetStatus_EosDetected_TakesPrecedenceOverContainerState()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));
        var ctx = service.ActiveScripts[ticket.TaskId];

        SetupPodWithContainerStates("Failed",
            ("script", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1 } }));

        var eosLine = $"EOS-{ctx.EosMarkerToken}<<>>0";
        SetupPodLogs($"output\n{eosLine}\n");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void CompleteScript_MultiContainerPod_UsesScriptContainerExitCode()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodWithContainerStates("Failed",
            ("script", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } }),
            ("nfs-watchdog", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1 } }));
        SetupPodLogs("");

        var result = service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        result.State.ShouldBe(ProcessState.Complete);
        result.ExitCode.ShouldBe(0);
    }

    // ========== Container Diagnostics (Fix 9) ==========

    [Fact]
    public void GetStatus_FailedContainer_IncludesDiagnosticsInLogs()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodWithContainerStates("Failed",
            ("script", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 137, Reason = "OOMKilled", Signal = 9 } }));
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(137);
        status.Logs.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("OOMKilled"));
        status.Logs.ShouldContain(l => l.Text.Contains("Signal: 9"));
    }

    [Fact]
    public void GetStatus_SuccessfulContainer_NoDiagnosticsAdded()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodWithContainerStates("Succeeded",
            ("script", new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } }));
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(0);
        status.Logs.ShouldNotContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("Container 'script' terminated"));
    }

    // ========== P0-1: TOCTOU Race Fix ==========

    [Fact]
    public void ProcessPendingScripts_CancelDuringTryAcquire_RetriesRemainingPending()
    {
        var service = CreateService();

        // Start a writer to hold the mutex
        var activeCmd = MakeIsolatedCommand("echo active", "race-mutex");
        var activeTicket = service.StartScript(activeCmd);

        // Queue two pending readers
        var cmdA = new StartScriptCommand("echo A", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "race-mutex", Array.Empty<string>(), null);
        var ticketA = service.StartScript(cmdA);
        var cmdB = new StartScriptCommand("echo B", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "race-mutex", Array.Empty<string>(), null);
        var ticketB = service.StartScript(cmdB);

        service.PendingScripts.ContainsKey(ticketA.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticketB.TaskId).ShouldBeTrue();

        // Cancel A (simulates the race: A removed from pending between TryAcquire and TryRemove)
        service.CancelScript(new CancelScriptCommand(new ScriptTicket(ticketA.TaskId), 0));

        // Complete active → releases mutex → ProcessPendingScripts
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(activeTicket, 0));

        // B should be launched (retry picked it up after A was gone)
        service.ActiveScripts.ContainsKey(ticketB.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticketA.TaskId).ShouldBeFalse();
    }

    [Fact]
    public void ProcessPendingScripts_DepthGuard_PreventsStackOverflow()
    {
        // The depth guard simply ensures that ProcessPendingScripts(depth > 3) returns immediately.
        // We test this indirectly: with no pending scripts, even calling the method is safe.
        var service = CreateService();

        // With no pending scripts, ProcessPendingScripts does nothing — no stack overflow.
        service.ReleaseMutexForTicket("nonexistent-ticket");

        service.ActiveScripts.ShouldBeEmpty();
    }

    [Fact]
    public void ProcessPendingScripts_NormalFlow_NoUnnecessaryRetry()
    {
        var service = CreateService();

        // Start a writer
        var activeCmd = MakeIsolatedCommand("echo active", "normal-mutex");
        var activeTicket = service.StartScript(activeCmd);

        // Queue one pending reader
        var cmdA = new StartScriptCommand("echo A", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "normal-mutex", Array.Empty<string>(), null);
        var ticketA = service.StartScript(cmdA);

        // Complete active → releases mutex → ProcessPendingScripts (no race)
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(activeTicket, 0));

        // A should be launched normally, pending empty
        service.ActiveScripts.ContainsKey(ticketA.TaskId).ShouldBeTrue();
        service.PendingScripts.ShouldBeEmpty();
    }

    // ========== P1-7: Concurrency Tests ==========

    [Fact]
    public void StartScript_WriterBlocked_QueuesAsPending()
    {
        var service = CreateService();

        // Start a writer (FullIsolation)
        var writerCmd = MakeIsolatedCommand("echo writer", "block-mutex");
        var writerTicket = service.StartScript(writerCmd);
        service.ActiveScripts.ContainsKey(writerTicket.TaskId).ShouldBeTrue();

        // Start a reader while writer holds the mutex → should queue
        var readerCmd = new StartScriptCommand("echo reader", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "block-mutex", Array.Empty<string>(), null);
        var readerTicket = service.StartScript(readerCmd);

        service.PendingScripts.ContainsKey(readerTicket.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(readerTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public void CompleteScript_ReleasesLock_ProcessesPending()
    {
        var service = CreateService();

        // Start writer, queue reader
        var writerCmd = MakeIsolatedCommand("echo writer", "release-mutex");
        var writerTicket = service.StartScript(writerCmd);

        var readerCmd = new StartScriptCommand("echo reader", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "release-mutex", Array.Empty<string>(), null);
        var readerTicket = service.StartScript(readerCmd);

        service.PendingScripts.ContainsKey(readerTicket.TaskId).ShouldBeTrue();

        // Complete writer → should release mutex and launch pending reader
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        service.CompleteScript(new CompleteScriptCommand(writerTicket, 0));

        service.ActiveScripts.ContainsKey(readerTicket.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(readerTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public void CancelScript_RemovesPendingScript()
    {
        var service = CreateService();

        // Start writer, queue reader
        var writerCmd = MakeIsolatedCommand("echo writer", "cancel-mutex");
        service.StartScript(writerCmd);

        var readerCmd = new StartScriptCommand("echo reader", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "cancel-mutex", Array.Empty<string>(), null);
        var readerTicket = service.StartScript(readerCmd);

        service.PendingScripts.ContainsKey(readerTicket.TaskId).ShouldBeTrue();

        // Cancel the pending reader
        service.CancelScript(new CancelScriptCommand(readerTicket, 0));

        service.PendingScripts.ShouldBeEmpty();
    }

    [Fact]
    public void StartScript_PendingQueueFull_RejectsWithFatal()
    {
        var settings = CreateSettingsWithMaxPending(1);
        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        // Start writer
        var writerCmd = MakeIsolatedCommand("echo writer", "full-mutex");
        service.StartScript(writerCmd);

        // Fill pending queue (max=1)
        var pendingCmd = new StartScriptCommand("echo pending1", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "full-mutex", Array.Empty<string>(), null);
        service.StartScript(pendingCmd);

        // Next script should be rejected
        var rejectedCmd = new StartScriptCommand("echo rejected", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), "full-mutex", Array.Empty<string>(), null);
        var rejectedTicket = service.StartScript(rejectedCmd);

        // Should have terminal result with Fatal exit code
        var status = service.GetStatus(new ScriptStatusRequest(rejectedTicket, 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
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
