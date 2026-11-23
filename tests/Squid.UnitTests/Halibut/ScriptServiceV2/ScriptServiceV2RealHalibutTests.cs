using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.UnitTests.Halibut.ScriptServiceV2;

/// <summary>
/// Fidelity tier: HIGH for serialization and service routing — a real agent <see cref="HalibutRuntime"/> and a
/// real server runtime talk mTLS over a real loopback socket, so every V2 message goes through Halibut's own
/// serializer. MEDIUM-MOCK for the agent behaviour: the V1/V2 services are trivial fakes (no processes).
/// Halibut resolves a service by <c>typeof(TContract).Name</c> (<c>IScriptService</c> vs <c>IScriptServiceV2</c>),
/// which these tests prove empirically: both register side by side, and a V1-only agent fails a V2 call while V1 keeps
/// working. NOTE (observed on Halibut 8.1.1943): the V2 failure is a plain <see cref="HalibutClientException"/>
/// ("Attempted to read past the end of the stream"), NOT a typed ServiceNotFound fault — the agent's type registry
/// is built from its registrations so it cannot even deserialise the unknown V2 request. A server must therefore
/// gate V2 on the advertised capability string, never probe by catching an exception.
/// </summary>
[Trait("Category", "RealHalibut")]
public sealed class ScriptServiceV2RealHalibutTests : IAsyncLifetime
{
    private static readonly ScriptTicket Ticket = new("v2-ticket");

    private readonly FakeV2Agent _v2 = new();
    private readonly FakeV1Agent _v1 = new();
    private HalibutRuntime _bothAgent;
    private HalibutRuntime _v1OnlyAgent;
    private HalibutRuntime _server;
    private ServiceEndPoint _bothEndpoint;
    private ServiceEndPoint _v1OnlyEndpoint;

    public Task InitializeAsync()
    {
        var limits = HalibutTimeoutsAndLimits.RecommendedValues();
        var serverCert = NewCert("CN=v2-server");
        var bothCert = NewCert("CN=v2-both-agent");
        var v1Cert = NewCert("CN=v2-v1only-agent");

        var both = new DelegateServiceFactory();
        both.Register<IScriptService, IScriptServiceAsync>(() => _v1);
        both.Register<IScriptServiceV2, IScriptServiceV2Async>(() => _v2);

        var v1Only = new DelegateServiceFactory();
        v1Only.Register<IScriptService, IScriptServiceAsync>(() => _v1);

        _bothAgent = StartAgent(both, bothCert, serverCert, limits, out var bothPort);
        _v1OnlyAgent = StartAgent(v1Only, v1Cert, serverCert, limits, out var v1Port);
        _server = new HalibutRuntimeBuilder().WithServerCertificate(serverCert).WithHalibutTimeoutsAndLimits(limits).Build();
        _bothEndpoint = new ServiceEndPoint(new Uri($"https://localhost:{bothPort}/"), bothCert.Thumbprint, limits);
        _v1OnlyEndpoint = new ServiceEndPoint(new Uri($"https://localhost:{v1Port}/"), v1Cert.Thumbprint, limits);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _bothAgent.DisposeAsync();
        await _v1OnlyAgent.DisposeAsync();
    }

    [Fact]
    public async Task V1AndV2RegisteredOnOneAgent_EachResolvesIndependently()
    {
        var v1Client = _server.CreateAsyncClient<IScriptService, IAsyncScriptService>(_bothEndpoint);
        var v2Client = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(_bothEndpoint);
        _v2.NextStatus = new ScriptStatusResponseV2(Ticket, ScriptStateV2.Running, 0, new List<ProcessOutput>(), 7);

        var v1 = await v1Client.GetStatusAsync(new ScriptStatusRequest(Ticket, 0));
        var v2 = await v2Client.GetStatusAsync(new ScriptStatusRequestV2(Ticket, 0));

        v1.ExitCode.ShouldBe(V1Marker, "V1 call must reach the V1 service");
        v2.State.ShouldBe(ScriptStateV2.Running);
        v2.NextLogSequence.ShouldBe(7, "V2 call must reach the V2 service");
        _v1.GetStatusCalls.ShouldBe(1);
        _v2.GetStatusCalls.ShouldBe(1);
    }

    [Fact]
    public async Task V1OnlyAgent_FailsV2Calls_AndV1KeepsWorking()
    {
        var v2Client = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(_v1OnlyEndpoint);
        var v1Client = _server.CreateAsyncClient<IScriptService, IAsyncScriptService>(_v1OnlyEndpoint);

        var thrown = await Should.ThrowAsync<HalibutClientException>(() => v2Client.GetStatusAsync(new ScriptStatusRequestV2(Ticket, 0)));
        var v1 = await v1Client.GetStatusAsync(new ScriptStatusRequest(Ticket, 0));

        thrown.ShouldNotBeNull();
        v1.ExitCode.ShouldBe(V1Marker);
    }

    [Fact]
    public async Task StartScriptCommandV2_EveryField_RoundTripsThroughHalibut()
    {
        var client = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(_bothEndpoint);
        var sent = new StartScriptCommandV2(Ticket, "echo hi", ScriptIsolationLevel.FullIsolation, TimeSpan.FromSeconds(30), "squid/machine/9", new[] { "a", "b c" }, "task-1", TimeSpan.FromSeconds(5))
        {
            ScriptSyntax = ScriptType.Python,
            TargetNamespace = "ns",
            Labels = new Dictionary<string, string> { ["k"] = "v" },
            Scripts = new Dictionary<ScriptType, string> { [ScriptType.PowerShell] = "Write-Host hi" },
            Files = new List<ScriptFile> { new("f.txt", DataStream.FromString("file-body"), "pw") }
        };
        _v2.NextStatus = new ScriptStatusResponseV2(Ticket, ScriptStateV2.Pending, 0, new List<ProcessOutput>(), 0);

        await client.StartScriptAsync(sent);

        var received = _v2.ReceivedStart;
        received.ScriptTicket.ShouldBe(Ticket);
        received.ScriptBody.ShouldBe("echo hi");
        received.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        received.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        received.IsolationMutexName.ShouldBe("squid/machine/9");
        received.Arguments.ShouldBe(new[] { "a", "b c" });
        received.TaskId.ShouldBe("task-1");
        received.DurationToWaitForScriptToFinish.ShouldBe(TimeSpan.FromSeconds(5));
        received.ScriptSyntax.ShouldBe(ScriptType.Python);
        received.TargetNamespace.ShouldBe("ns");
        received.Labels.ShouldBe(new Dictionary<string, string> { ["k"] = "v" });
        received.Scripts.ShouldBe(new Dictionary<ScriptType, string> { [ScriptType.PowerShell] = "Write-Host hi" });
        received.Files.Single().Name.ShouldBe("f.txt");
        received.Files.Single().EncryptionPassword.ShouldBe("pw");
        _v2.ReceivedFileBody.ShouldBe("file-body", "DataStream content must survive the V2 Start call");
    }

    [Fact]
    public async Task RequestsAndCommands_RoundTripThroughHalibut()
    {
        var client = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(_bothEndpoint);
        _v2.NextStatus = new ScriptStatusResponseV2(Ticket, ScriptStateV2.Running, 0, new List<ProcessOutput>(), 0);
        _v2.NextComplete = new CompleteScriptResponseV2(Ticket, CompleteScriptOutcomeV2.AlreadyCompleted, null);

        await client.GetStatusAsync(new ScriptStatusRequestV2(Ticket, 41));
        await client.CancelScriptAsync(new CancelScriptCommandV2(Ticket, 42));
        await client.CompleteScriptAsync(new CompleteScriptCommandV2(Ticket, 43));

        _v2.ReceivedStatusCursor.ShouldBe(41L);
        _v2.ReceivedCancelCursor.ShouldBe(42L);
        _v2.ReceivedCompleteCursor.ShouldBe(43L);
        _v2.ReceivedTickets.ShouldAllBe(t => t == Ticket);
    }

    [Theory]
    [InlineData(ScriptStateV2.Pending, 0)]
    [InlineData(ScriptStateV2.Running, 0)]
    [InlineData(ScriptStateV2.Completed, -1)]
    [InlineData(ScriptStateV2.Completed, 0)]
    [InlineData(ScriptStateV2.Completed, -43)]
    [InlineData(ScriptStateV2.UnknownTicket, 0)]
    [InlineData(ScriptStateV2.ResultLost, 0)]
    public async Task ScriptStatusResponseV2_EveryState_RoundTripsThroughHalibut(ScriptStateV2 state, int exitCode)
    {
        var client = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(_bothEndpoint);
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var logs = new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "out", at), new(ProcessOutputSource.StdErr, "err", at) };
        _v2.NextStatus = new ScriptStatusResponseV2(Ticket, state, exitCode, logs, 99);

        var received = await client.GetStatusAsync(new ScriptStatusRequestV2(Ticket, 0));

        AssertSameStatus(received, _v2.NextStatus);
        received.State.ShouldBe(state);
        received.ExitCode.ShouldBe(exitCode);
        received.NextLogSequence.ShouldBe(99);
    }

    [Theory]
    [InlineData(CompleteScriptOutcomeV2.Completed, true)]
    [InlineData(CompleteScriptOutcomeV2.AlreadyCompleted, false)]
    [InlineData(CompleteScriptOutcomeV2.UnknownTicket, false)]
    public async Task CompleteScriptResponseV2_EveryOutcome_RoundTripsThroughHalibut(CompleteScriptOutcomeV2 outcome, bool withFinalStatus)
    {
        var client = _server.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(_bothEndpoint);
        var final = withFinalStatus ? new ScriptStatusResponseV2(Ticket, ScriptStateV2.Completed, 3, new List<ProcessOutput> { new(ProcessOutputSource.Debug, "tail", DateTimeOffset.UnixEpoch) }, 5) : null;
        _v2.NextComplete = new CompleteScriptResponseV2(Ticket, outcome, final);

        var received = await client.CompleteScriptAsync(new CompleteScriptCommandV2(Ticket, 0));

        received.Outcome.ShouldBe(outcome);
        (received.FinalStatus != null).ShouldBe(withFinalStatus);
        received.Ticket.ShouldBe(Ticket);
        if (withFinalStatus) AssertSameStatus(received.FinalStatus, final);
    }

    private const int V1Marker = 1234;

    private static void AssertSameStatus(ScriptStatusResponseV2 actual, ScriptStatusResponseV2 expected)
    {
        actual.Ticket.ShouldBe(expected.Ticket);
        actual.State.ShouldBe(expected.State);
        actual.ExitCode.ShouldBe(expected.ExitCode);
        actual.NextLogSequence.ShouldBe(expected.NextLogSequence);
        actual.Logs.Select(l => (l.Source, l.Text, l.Occurred.UtcDateTime)).ShouldBe(expected.Logs.Select(l => (l.Source, l.Text, l.Occurred.UtcDateTime)));
    }

    private static HalibutRuntime StartAgent(DelegateServiceFactory factory, X509Certificate2 cert, X509Certificate2 serverCert, HalibutTimeoutsAndLimits limits, out int port)
    {
        var agent = new HalibutRuntimeBuilder().WithServiceFactory(factory).WithServerCertificate(cert).WithHalibutTimeoutsAndLimits(limits).Build();
        agent.Trust(serverCert.Thumbprint);
        port = agent.Listen();

        return agent;
    }

    private static X509Certificate2 NewCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
    }

    private sealed class FakeV1Agent : IScriptServiceAsync
    {
        private int _getStatusCalls;

        public int GetStatusCalls => _getStatusCalls;

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _getStatusCalls);

            return Task.FromResult(new ScriptStatusResponse(request.Ticket, ProcessState.Running, V1Marker, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeV2Agent : IScriptServiceV2Async
    {
        private int _getStatusCalls;

        public ScriptStatusResponseV2 NextStatus { get; set; }

        public CompleteScriptResponseV2 NextComplete { get; set; }

        public StartScriptCommandV2 ReceivedStart { get; private set; }

        public string ReceivedFileBody { get; private set; }

        public long ReceivedStatusCursor { get; private set; }

        public long ReceivedCancelCursor { get; private set; }

        public long ReceivedCompleteCursor { get; private set; }

        public List<ScriptTicket> ReceivedTickets { get; } = new();

        public int GetStatusCalls => _getStatusCalls;

        public async Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command, CancellationToken ct)
        {
            ReceivedStart = command;

            await command.Files.Single().Contents.Receiver().ReadAsync(async (stream, token) =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                ReceivedFileBody = await reader.ReadToEndAsync(token);
            }, ct);

            return NextStatus;
        }

        public Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request, CancellationToken ct)
        {
            Interlocked.Increment(ref _getStatusCalls);
            ReceivedStatusCursor = request.LastLogSequence;
            ReceivedTickets.Add(request.Ticket);

            return Task.FromResult(NextStatus);
        }

        public Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command, CancellationToken ct)
        {
            ReceivedCancelCursor = command.LastLogSequence;
            ReceivedTickets.Add(command.Ticket);

            return Task.FromResult(NextStatus);
        }

        public Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command, CancellationToken ct)
        {
            ReceivedCompleteCursor = command.LastLogSequence;
            ReceivedTickets.Add(command.Ticket);

            return Task.FromResult(NextComplete);
        }
    }
}
