using System;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Collection(TentacleMetricsCollection.Name)]
public class ScriptRecoveryServiceTests : IDisposable
{
    private readonly string _tempWorkspace;
    private readonly ScriptRecoveryService _recovery = new();
    private readonly ScriptIsolationMutex _mutex = new();
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly KubernetesPodManager _podManager;
    private readonly ScriptPodService _service;

    public ScriptRecoveryServiceTests()
    {
        DiskSpaceChecker.Enabled = false;

        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-recovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        _ops = new Mock<IKubernetesPodOperations>();
        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var k8sSettings = new KubernetesSettings { TentacleNamespace = "test-ns" };
        _podManager = new KubernetesPodManager(_ops.Object, k8sSettings);

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        _service = new ScriptPodService(tentacleSettings, k8sSettings, _podManager);
    }

    [Fact]
    public void Recovery_NoStateFiles_NoAction()
    {
        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(0);
        _service.ActiveScripts.ShouldBeEmpty();
    }

    [Fact]
    public void Recovery_EmptyWorkspace_NoError()
    {
        var nonexistent = Path.Combine(_tempWorkspace, "does-not-exist");

        var count = _recovery.RecoverScripts(nonexistent, _service, _podManager, _mutex);

        count.ShouldBe(0);
    }

    [Fact]
    public void Recovery_PodStillRunning_RebuildsContext()
    {
        var ticketId = "running-ticket";
        SeedStateFile(ticketId, "squid-script-running", "NoIsolation");
        SetupPodPhase("squid-script-running", "Running");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(1);
        _service.ActiveScripts.ContainsKey(ticketId).ShouldBeTrue();
        _service.ActiveScripts[ticketId].PodName.ShouldBe("squid-script-running");
    }

    [Fact]
    public void Recovery_PodCompleted_InjectsTerminalResult()
    {
        var ticketId = "completed-ticket";
        SeedStateFile(ticketId, "squid-script-completed", "NoIsolation");
        SetupPodPhase("squid-script-completed", "Succeeded");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(1);
        _service.ActiveScripts.ContainsKey(ticketId).ShouldBeFalse();

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void Recovery_PodNotFound_InjectsUnknownResult()
    {
        var ticketId = "missing-ticket";
        SeedStateFile(ticketId, "squid-script-missing", "NoIsolation");
        SetupPodPhase("squid-script-missing", KubernetesPodManager.PhaseNotFound);

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(1);
        _service.ActiveScripts.ContainsKey(ticketId).ShouldBeFalse();

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void Recovery_FullIsolation_ReacquiresMutex()
    {
        var ticketId = "isolated-ticket";
        SeedStateFile(ticketId, "squid-script-isolated", "FullIsolation", "deploy-mutex");
        SetupPodPhase("squid-script-isolated", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.MutexLocks.ContainsKey(ticketId).ShouldBeTrue();
    }

    [Fact]
    public void Recovery_NoIsolation_ReacquiresReaderLock()
    {
        var ticketId = "reader-ticket";
        SeedStateFile(ticketId, "squid-script-reader", "NoIsolation");
        SetupPodPhase("squid-script-reader", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.MutexLocks.ContainsKey(ticketId).ShouldBeTrue();
    }

    [Fact]
    public void Recovery_CorruptStateFile_SkipsGracefully()
    {
        var ticketDir = Path.Combine(_tempWorkspace, "corrupt-ticket");
        Directory.CreateDirectory(ticketDir);
        File.WriteAllText(Path.Combine(ticketDir, ".squid-state.json"), "not json");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(0);
        _service.ActiveScripts.ShouldBeEmpty();
    }

    [Fact]
    public void Recovery_MultipleScripts_RecoversAll()
    {
        SeedStateFile("ticket-a", "squid-script-a", "NoIsolation");
        SeedStateFile("ticket-b", "squid-script-b", "NoIsolation");
        SetupPodPhase("squid-script-a", "Running");
        SetupPodPhase("squid-script-b", "Running");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(2);
        _service.ActiveScripts.Count.ShouldBe(2);
    }

    [Fact]
    public void Recovery_PodRunning_RestoresLastLogTimestamp()
    {
        var ticketId = "timestamp-ticket";
        var timestamp = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        SeedStateFileWithTimestamp(ticketId, "squid-script-ts", "NoIsolation", timestamp);
        SetupPodPhase("squid-script-ts", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.ActiveScripts.TryGetValue(ticketId, out var ctx).ShouldBeTrue();
        ctx.LastLogTimestamp.ShouldBe(timestamp);
    }

    [Fact]
    public void Recovery_NullTimestamp_DefaultsToNull()
    {
        var ticketId = "null-ts-ticket";
        SeedStateFile(ticketId, "squid-script-nullts", "NoIsolation");
        SetupPodPhase("squid-script-nullts", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.ActiveScripts.TryGetValue(ticketId, out var ctx).ShouldBeTrue();
        ctx.LastLogTimestamp.ShouldBeNull();
    }

    private void SeedStateFileWithTimestamp(string ticketId, string podName, string isolation, DateTime? lastLogTimestamp, string? mutexName = null)
    {
        var workDir = Path.Combine(_tempWorkspace, ticketId);
        Directory.CreateDirectory(workDir);

        ScriptStateFile.Write(workDir, new ScriptStateFile
        {
            TicketId = ticketId,
            PodName = podName,
            EosMarkerToken = "test-eos-token",
            Isolation = isolation,
            IsolationMutexName = mutexName,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLogTimestamp = lastLogTimestamp
        });
    }

    private void SeedStateFile(string ticketId, string podName, string isolation, string? mutexName = null)
    {
        var workDir = Path.Combine(_tempWorkspace, ticketId);
        Directory.CreateDirectory(workDir);

        ScriptStateFile.Write(workDir, new ScriptStateFile
        {
            TicketId = ticketId,
            PodName = podName,
            EosMarkerToken = "test-eos-token",
            Isolation = isolation,
            IsolationMutexName = mutexName,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    [Fact]
    public void RecoverPending_InjectsTerminalFailure_InsteadOfResubmit()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };

        var secret = new k8s.Models.V1Secret
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "squid-pending-abc123" },
            StringData = new Dictionary<string, string>
            {
                ["ticketId"] = "abc123456789extra",
                ["scriptBody"] = "echo recovered",
                ["isolation"] = "NoIsolation",
                ["isolationMutexName"] = "",
                ["targetNamespace"] = "",
                ["enqueuedAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        ops.Setup(o => o.ListSecrets("test-ns", "squid.io/context-type=pending-script"))
            .Returns(new k8s.Models.V1SecretList { Items = new List<k8s.Models.V1Secret> { secret } });

        var podManager = new KubernetesPodManager(ops.Object, settings);
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var service = new ScriptPodService(tentacleSettings, settings, podManager);

        _recovery.RecoverPendingScripts(ops.Object, settings, service);

        // Should NOT be re-launched as active script
        service.ActiveScripts.Count.ShouldBe(0);

        // Should have terminal result injected
        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("abc123456789extra"), 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void RecoverPending_DeletesSecretAfterInjection()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };

        var secret = new k8s.Models.V1Secret
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "squid-pending-xyz789" },
            StringData = new Dictionary<string, string>
            {
                ["ticketId"] = "xyz789012345extra",
                ["scriptBody"] = "echo test",
                ["isolation"] = "NoIsolation",
                ["isolationMutexName"] = "",
                ["targetNamespace"] = ""
            }
        };

        ops.Setup(o => o.ListSecrets("test-ns", "squid.io/context-type=pending-script"))
            .Returns(new k8s.Models.V1SecretList { Items = new List<k8s.Models.V1Secret> { secret } });

        var podManager = new KubernetesPodManager(ops.Object, settings);
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var service = new ScriptPodService(tentacleSettings, settings, podManager);

        _recovery.RecoverPendingScripts(ops.Object, settings, service);

        ops.Verify(o => o.DeleteSecret("squid-pending-xyz789", "test-ns"), Times.Once);
    }

    [Fact]
    public void RecoverPending_NoSecrets_NoAction()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };

        ops.Setup(o => o.ListSecrets("test-ns", "squid.io/context-type=pending-script"))
            .Returns(new k8s.Models.V1SecretList { Items = new List<k8s.Models.V1Secret>() });

        var podManager = new KubernetesPodManager(ops.Object, settings);
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var service = new ScriptPodService(tentacleSettings, settings, podManager);

        _recovery.RecoverPendingScripts(ops.Object, settings, service);

        service.ActiveScripts.Count.ShouldBe(0);
    }

    // ========== Completed-pod recovery: real exit code + logs ==========

    [Theory]
    [InlineData("Succeeded", 0, null)]
    [InlineData("Failed", 3, "Error")]
    [InlineData("Failed", 137, "OOMKilled")]
    public void Recovery_CompletedPod_ReportsRealExitCode(string phase, int exitCode, string? reason)
    {
        SeedStateFile("done-ticket", "squid-script-done", "NoIsolation");
        SetupCompletedPod("squid-script-done", phase, ScriptContainer(exitCode, reason));

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("done-ticket"), 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(exitCode);
    }

    [Fact]
    public void Recovery_CompletedPod_CompleteScriptReturnsRealExitCodeAndLogs()
    {
        SeedStateFile("logs-ticket", "squid-script-logs", "NoIsolation");
        SetupCompletedPod("squid-script-logs", "Succeeded", ScriptContainer(0, null));
        SetupPodLogs("hello from the pod\nsecond line\n");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        var result = _service.CompleteScript(new CompleteScriptCommand(new ScriptTicket("logs-ticket"), 0));
        result.State.ShouldBe(ProcessState.Complete);
        result.ExitCode.ShouldBe(0);
        result.Logs.Select(l => l.Text).ShouldContain(t => t.Contains("hello from the pod"));
        result.Logs.Select(l => l.Text).ShouldContain(t => t.Contains("second line"));
    }

    [Fact]
    public void Recovery_CompletedPod_CleansUpPodWorkspaceAndMutexExactlyOnce()
    {
        SeedStateFile("clean-ticket", "squid-script-clean", "NoIsolation");
        SetupCompletedPod("squid-script-clean", "Failed", ScriptContainer(3, "Error"));
        SetupPodLogs("boom\n");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);
        _service.CompleteScript(new CompleteScriptCommand(new ScriptTicket("clean-ticket"), 0));

        _ops.Verify(o => o.DeletePod("squid-script-clean", It.IsAny<string>(), It.IsAny<int?>()), Times.Once);
        Directory.Exists(Path.Combine(_tempWorkspace, "clean-ticket")).ShouldBeFalse();
        _service.MutexLocks.ContainsKey("clean-ticket").ShouldBeFalse();
        _service.ActiveScripts.ContainsKey("clean-ticket").ShouldBeFalse();
    }

    [Fact]
    public void Recovery_CompletedPodWithSidecar_PicksScriptContainer()
    {
        SeedStateFile("sidecar-ticket", "squid-script-sidecar", "NoIsolation");
        var sidecar = new V1ContainerStatus { Name = "nfs-watchdog", State = Terminated(9, "Error") };
        SetupCompletedPod("squid-script-sidecar", "Succeeded", sidecar, ScriptContainer(0, null));

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("sidecar-ticket"), 0));
        status.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void Recovery_CompletedPod_LogReadFails_StillReportsRealExitCode()
    {
        SeedStateFile("nolog-ticket", "squid-script-nolog", "NoIsolation");
        SetupCompletedPod("squid-script-nolog", "Failed", ScriptContainer(3, "Error"));
        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
            .Throws(new InvalidOperationException("logs unavailable"));

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        var result = _service.CompleteScript(new CompleteScriptCommand(new ScriptTicket("nolog-ticket"), 0));
        result.ExitCode.ShouldBe(3);
    }

    [Fact]
    public void Recovery_CompletedPod_NoScriptContainerStatus_FallsBackToUnknownWithReason()
    {
        SeedStateFile("nostatus-ticket", "squid-script-nostatus", "NoIsolation");
        SetupCompletedPod("squid-script-nostatus", "Succeeded", new V1ContainerStatus { Name = "nfs-watchdog", State = Terminated(0, null) });

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("nostatus-ticket"), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
        status.Logs.Select(l => l.Text).ShouldContain(t => t.Contains("exit code of container 'script' could not be read"));
        _service.ActiveScripts.ContainsKey("nostatus-ticket").ShouldBeFalse();
    }

    [Fact]
    public void Recovery_CompletedPod_ScriptContainerNotTerminated_FallsBackToUnknown()
    {
        SeedStateFile("waiting-ticket", "squid-script-waiting", "NoIsolation");
        SetupCompletedPod("squid-script-waiting", "Failed", new V1ContainerStatus { Name = "script", State = new V1ContainerState { Waiting = new V1ContainerStateWaiting() } });

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("waiting-ticket"), 0)).ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void Recovery_CompletedPod_StatusReadFailsAfterPhase_FallsBackToUnknown()
    {
        SeedStateFile("apierr-ticket", "squid-script-apierr", "NoIsolation");
        _ops.SetupSequence(o => o.ReadPodStatus("squid-script-apierr", It.IsAny<string>()))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Failed" } })
            .Throws(new InvalidOperationException("apiserver unavailable"));

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("apierr-ticket"), 0)).ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    // Pins SSOT: recovery and the normal completion path must derive the exit code from the same
    // pod status. A parallel parser in recovery would diverge on at least one of these shapes.
    [Theory]
    [InlineData("Succeeded", 0)]
    [InlineData("Failed", 3)]
    [InlineData("Failed", 137)]
    public void Recovery_CompletedPod_ExitCodeMatchesNormalCompletionPath(string phase, int exitCode)
    {
        var sidecar = new V1ContainerStatus { Name = "nfs-watchdog", State = Terminated(1, null) };

        SeedStateFile("parity-ticket", "squid-script-parity", "NoIsolation");
        SetupCompletedPod("squid-script-parity", phase, sidecar, ScriptContainer(exitCode, null));
        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);
        var recovered = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("parity-ticket"), 0)).ExitCode;

        SetupCompletedPod("squid-script-normal", phase, sidecar, ScriptContainer(exitCode, null));
        _service.RestoreActiveScript(new ScriptPodContext("normal-ticket", "squid-script-normal", _tempWorkspace, "eos"), null);
        var normal = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("normal-ticket"), 0)).ExitCode;

        recovered.ShouldBe(normal);
        recovered.ShouldBe(exitCode);
    }

    // Guards RestoreScriptContext's ExtractSensitiveValues: without it a recovered script's logs
    // (now returned for completed pods) would carry secrets unmasked.
    [Theory]
    [InlineData("Running")]
    [InlineData("Succeeded")]
    public void Recovery_RecoveredScript_MasksSensitiveValuesInReturnedLogs(string phaseAtRecovery)
    {
        const string secret = "Sup3r-S3cret-Value-42";
        SeedStateFile("mask-ticket", "squid-script-mask", "NoIsolation");
        WriteEncryptedSensitiveVariables(Path.Combine(_tempWorkspace, "mask-ticket"), "pw-for-recovery-test", secret);
        SetupPodLogs($"connecting with {secret}\nplain line\n");

        var reads = 0;
        _ops.Setup(o => o.ReadPodStatus("squid-script-mask", It.IsAny<string>()))
            .Returns(() => reads++ == 0 && phaseAtRecovery == "Running" ? PodInPhase("Running", null) : PodInPhase("Succeeded", ScriptContainer(0, null)));

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        var texts = _service.CompleteScript(new CompleteScriptCommand(new ScriptTicket("mask-ticket"), 0)).Logs.Select(l => l.Text).ToList();
        texts.ShouldNotContain(t => t.Contains(secret));
        texts.ShouldContain(t => t.Contains("connecting with") && t.Contains("*"));
        texts.ShouldContain(t => t.Contains("plain line"));
    }

    private static V1Pod PodInPhase(string phase, V1ContainerStatus? container) => new() { Status = new V1PodStatus { Phase = phase, ContainerStatuses = container == null ? null : new[] { container } } };

    private static void WriteEncryptedSensitiveVariables(string workDir, string password, string secret)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["dbPwd"] = secret }));
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);

        using var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt, 600_000, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using (var aes = new System.Security.Cryptography.AesGcm(kdf.GetBytes(32), 16))
            aes.Encrypt(nonce, plain, cipher, tag);

        var envelope = "V2__"u8.ToArray().Concat(salt).Concat(nonce).Concat(cipher).Concat(tag).ToArray();

        File.WriteAllBytes(Path.Combine(workDir, "sensitiveVariables.json"), envelope);
        File.WriteAllText(Path.Combine(workDir, "sensitiveVariables.json.key"), password);
    }

    private static V1ContainerStatus ScriptContainer(int exitCode, string? reason) => new() { Name = "script", State = Terminated(exitCode, reason) };

    private static V1ContainerState Terminated(int exitCode, string? reason) => new() { Terminated = new V1ContainerStateTerminated { ExitCode = exitCode, Reason = reason } };

    private void SetupCompletedPod(string podName, string phase, params V1ContainerStatus[] containers)
    {
        _ops.Setup(o => o.ReadPodStatus(podName, It.IsAny<string>()))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase, ContainerStatuses = containers } });
    }

    private void SetupPodLogs(string logs)
    {
        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
            .Returns(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs)));
    }

    private void SetupPodPhase(string podName, string phase)
    {
        if (phase == KubernetesPodManager.PhaseNotFound)
        {
            _ops.Setup(o => o.ReadPodStatus(podName, It.IsAny<string>()))
                .Throws(new k8s.Autorest.HttpOperationException
                {
                    Response = new k8s.Autorest.HttpResponseMessageWrapper(
                        new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty)
                });
        }
        else
        {
            _ops.Setup(o => o.ReadPodStatus(podName, It.IsAny<string>()))
                .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase } });
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
                Directory.Delete(_tempWorkspace, recursive: true);
        }
        catch { }
    }
}
