using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Halibut;

/// <summary>
/// Fidelity tier: HIGH — the REAL <see cref="TentacleHalibutHost"/> (listening on a real loopback port over mTLS) wrapping
/// a real <see cref="LocalScriptService"/> that runs real bash, built through the same <c>TentacleApp.BuildScriptService</c>
/// seam production uses; a separate server <see cref="HalibutRuntime"/> calls it with real Halibut proxies, so every V1 and
/// V2 message goes through Halibut's serializer and service routing.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class TentacleHalibutHostScriptServiceV2Tests : IAsyncLifetime
{
    private readonly string _tombstoneDir = Path.Combine(Path.GetTempPath(), $"squid-v2-host-{Guid.NewGuid():N}");
    private readonly List<string> _tickets = new();
    private readonly HalibutTimeoutsAndLimits _limits = HalibutTimeoutsAndLimits.RecommendedValues();
    private X509Certificate2 _agentCert = null!;
    private X509Certificate2 _serverCert = null!;
    private TentacleHalibutHost _bothHost = null!;
    private TentacleHalibutHost _v1OnlyHost = null!;
    private HalibutRuntime _server = null!;

    public Task InitializeAsync()
    {
        _agentCert = NewCert("CN=v2-agent");
        _serverCert = NewCert("CN=v2-server");

        var backend = new LocalScriptService(new ScriptStateStoreFactory(), null, new ScriptTombstoneStore(_tombstoneDir));
        _bothHost = StartHost(TentacleApp.BuildScriptService(backend));
        _v1OnlyHost = StartHost(TentacleApp.BuildScriptService(new FakeScriptBackend()));
        _server = new HalibutRuntimeBuilder().WithServerCertificate(_serverCert).WithHalibutTimeoutsAndLimits(_limits).Build();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _bothHost.DisposeAsync();
        await _v1OnlyHost.DisposeAsync();

        var cleaner = new LocalScriptService(new ScriptStateStoreFactory(), null, new ScriptTombstoneStore(_tombstoneDir));

        foreach (var id in _tickets)
        {
            try { cleaner.CancelScript(new CancelScriptCommandV2(new ScriptTicket(id), 0)); } catch { }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), $"squid-tentacle-{id}"), recursive: true); } catch { }
        }

        try { Directory.Delete(_tombstoneDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task V2_StartStatusComplete_EndToEnd_RealBashScript()
    {
        if (OperatingSystem.IsWindows()) return;

        var v2 = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(Endpoint(_bothHost));
        var ticket = NewTicket();

        var started = await v2.StartScriptAsync(new StartScriptCommandV2 { ScriptTicket = ticket, ScriptBody = "echo over-halibut; sleep 1; exit 5", ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(1) });
        var final = await PollUntilCompletedAsync(v2, ticket);
        var complete = await v2.CompleteScriptAsync(new CompleteScriptCommandV2(ticket, 0));
        var repeat = await v2.CompleteScriptAsync(new CompleteScriptCommandV2(ticket, 0));
        var unknown = await v2.GetStatusAsync(new ScriptStatusRequestV2(NewTicket(), 0));

        started.State.ShouldBe(ScriptStateV2.Running);
        final.ExitCode.ShouldBe(5);
        final.Logs.Any(l => l.Text == "over-halibut").ShouldBeTrue();
        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(5);
        repeat.Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
        unknown.State.ShouldBe(ScriptStateV2.UnknownTicket);
    }

    [Fact]
    public async Task V2_CompleteOfAScriptStillRunning_ReturnsNotYetCompleteOverHalibut_NotAServiceException()
    {
        if (OperatingSystem.IsWindows()) return;

        var v2 = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(Endpoint(_bothHost));
        using var sleeper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        var ticket = NewTicket();
        new ScriptStateStore(Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}")).Save(new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, ProcessId = sleeper.Id, ProcessStartedAt = sleeper.StartTime.ToUniversalTime(), CreatedAt = DateTimeOffset.UtcNow });

        try
        {
            var response = await v2.CompleteScriptAsync(new CompleteScriptCommandV2(ticket, 0));

            response.Outcome.ShouldBe(CompleteScriptOutcomeV2.NotYetComplete);
            response.FinalStatus!.State.ShouldBe(ScriptStateV2.Running);
        }
        finally { sleeper.Kill(); }
    }

    [Fact]
    public async Task V2_StartWithoutSettingMutexTimeout_StillRuns()
    {
        if (OperatingSystem.IsWindows()) return;

        var v2 = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(Endpoint(_bothHost));
        var ticket = NewTicket();

        await v2.StartScriptAsync(new StartScriptCommandV2 { ScriptTicket = ticket, ScriptBody = "exit 0" });
        var final = await PollUntilCompletedAsync(v2, ticket);

        final.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task V1_StillWorksOnTheSameAgent_ThatAlsoServesV2()
    {
        if (OperatingSystem.IsWindows()) return;

        var v1 = _server.CreateAsyncClient<IScriptService, IAsyncScriptService>(Endpoint(_bothHost));
        var ticket = NewTicket();
        var command = new StartScriptCommand(ticket, "echo v1-path; exit 3", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, Array.Empty<string>(), null, TimeSpan.FromSeconds(10));

        var started = await v1.StartScriptAsync(command);
        var completed = await v1.CompleteScriptAsync(new CompleteScriptCommand(ticket, 0));

        started.State.ShouldBe(ProcessState.Complete);
        completed.ExitCode.ShouldBe(3);
        completed.Logs.Any(l => l.Text == "v1-path").ShouldBeTrue();
    }

    [Fact]
    public async Task Capabilities_AdvertiseV2_ExactlyWhenTheHostRegistersIt()
    {
        var bothCaps = await _server.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(Endpoint(_bothHost)).GetCapabilitiesAsync(new CapabilitiesRequest());
        var v1OnlyCaps = await _server.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(Endpoint(_v1OnlyHost)).GetCapabilitiesAsync(new CapabilitiesRequest());

        bothCaps.SupportedServices.ShouldContain(ScriptServiceCapabilities.V2);
        bothCaps.SupportedServices.ShouldContain("IScriptService/v1");
        v1OnlyCaps.SupportedServices.ShouldNotContain(ScriptServiceCapabilities.V2, "an agent that did not register V2 must not advertise it");
        ScriptServiceCapabilities.V2.ShouldBe("IScriptService/v2");
    }

    [Fact]
    public async Task V1OnlyAgent_KeepsServingV1_ForOldServers()
    {
        var v1 = _server.CreateAsyncClient<IScriptService, IAsyncScriptService>(Endpoint(_v1OnlyHost));

        var status = await v1.GetStatusAsync(new ScriptStatusRequest(new ScriptTicket("fake-ticket"), 0));

        status.State.ShouldBe(ProcessState.Running, "the V1-only fake backend's canned answer must come back unchanged");
    }

    private static async Task<ScriptStatusResponseV2> PollUntilCompletedAsync(IAsyncScriptServiceV2 v2, ScriptTicket ticket)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await v2.GetStatusAsync(new ScriptStatusRequestV2(ticket, 0));

            if (status.State == ScriptStateV2.Completed) return status;

            await Task.Delay(50);
        }

        throw new TimeoutException($"ticket {ticket.TaskId} did not reach Completed within 20s over Halibut — check the agent workspace squid-tentacle-{ticket.TaskId}");
    }

    private TentacleHalibutHost StartHost(IScriptService scriptService)
    {
        var caps = new CapabilitiesService(metadata: null, advertiseScriptServiceV2: scriptService is IScriptServiceV2);
        var host = new TentacleHalibutHost(_agentCert, scriptService, new TentacleSettings(), caps);

        host.StartListening(0, _serverCert.Thumbprint);

        return host;
    }

    private ServiceEndPoint Endpoint(TentacleHalibutHost host) => new(new Uri($"https://localhost:{host.ListeningPort}/"), _agentCert.Thumbprint, _limits);

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket.TaskId);
        return ticket;
    }

    private static X509Certificate2 NewCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }
}
