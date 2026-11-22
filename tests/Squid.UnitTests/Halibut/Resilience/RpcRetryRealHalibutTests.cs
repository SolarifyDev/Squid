using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.UnitTests.Halibut.Resilience;

/// <summary>
/// Fidelity tier: HIGH for the transport, MEDIUM-MOCK for the agent. Two real <see cref="HalibutRuntime"/>s
/// talk mTLS over real sockets through a TCP proxy that is severed mid-script and restored seconds later
/// (a real dropped connection followed by connection-refused while the "network" is down); the
/// production observer + <see cref="RetryingScriptClient"/> drive it. Only the agent's script service
/// is scripted (Running for a few seconds, then Complete) instead of launching a process.
/// The control test proves the blip is real: with the raw client the same outage surfaces a
/// <see cref="HalibutClientException"/> — exactly what used to pause the deployment.
/// </summary>
[Trait("Category", "RealHalibut")]
public sealed class RpcRetryRealHalibutTests : IAsyncLifetime
{
    private static readonly TimeSpan ScriptDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Outage = TimeSpan.FromSeconds(2);

    private HalibutRuntime _agent;
    private HalibutRuntime _server;
    private ToggleProxy _proxy;
    private ServiceEndPoint _endpoint;
    private ScriptedAgent _agentService;

    public Task InitializeAsync()
    {
        var limits = HalibutTimeoutsAndLimits.RecommendedValues();
        limits.RetryCountLimit = 1;
        limits.RetryListeningSleepInterval = TimeSpan.FromMilliseconds(100);
        limits.TcpClientConnectTimeout = TimeSpan.FromSeconds(2);

        var agentCert = NewCert("CN=rpc-retry-agent");
        var serverCert = NewCert("CN=rpc-retry-server");

        _agentService = new ScriptedAgent(ScriptDuration);
        var factory = new DelegateServiceFactory();
        factory.Register<IScriptService, IScriptServiceAsync>(() => new AsyncAdapter(_agentService));

        _agent = new HalibutRuntimeBuilder().WithServiceFactory(factory).WithServerCertificate(agentCert).WithHalibutTimeoutsAndLimits(limits).Build();
        _agent.Trust(serverCert.Thumbprint);
        var agentPort = _agent.Listen();

        _server = new HalibutRuntimeBuilder().WithServerCertificate(serverCert).WithHalibutTimeoutsAndLimits(limits).Build();
        _proxy = new ToggleProxy(agentPort);
        _endpoint = new ServiceEndPoint(new Uri($"https://localhost:{_proxy.Port}/"), agentCert.Thumbprint, limits);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _proxy.Dispose();
        await _server.DisposeAsync();
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionDroppedMidScript_WithRetries_ScriptCompletesWithoutThrowing()
    {
        var client = new RetryingScriptClient(_server.CreateAsyncClient<IScriptService, IAsyncScriptService>(_endpoint), RpcRetryHandler.FromEnvironment(), TimeSpan.FromMinutes(5), CancellationToken.None);

        var result = await ObserveWithBlipAsync(client);

        result.Success.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);
        result.LogLines.ShouldContain("script-output");
        _agentService.CompleteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Control_ConnectionDroppedMidScript_WithoutRetries_SurfacesTheTransientFailure()
    {
        var raw = _server.CreateAsyncClient<IScriptService, IAsyncScriptService>(_endpoint);

        var thrown = await Should.ThrowAsync<Exception>(() => ObserveWithBlipAsync(raw));

        TransientFailureClassifier.IsTransient(thrown).ShouldBeTrue("this is the exception that makes the runner pause the deployment");
    }

    private async Task<Squid.Core.Services.DeploymentExecution.Script.ScriptExecutionResult> ObserveWithBlipAsync(IAsyncScriptService client)
    {
        var ticket = new ScriptTicket("real-halibut-ticket");
        var start = await client.StartScriptAsync(new StartScriptCommand(ticket, "echo", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(1), "m", Array.Empty<string>(), "1", TimeSpan.Zero, Array.Empty<ScriptFile>()));
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 50, MaxPollIntervalMs = 200 });

        var observing = observer.ObserveAndCompleteAsync(new Machine { Name = "real-agent" }, client, ticket, TimeSpan.FromMinutes(5), CancellationToken.None, null, start);

        await Task.Delay(TimeSpan.FromMilliseconds(800));
        _proxy.Sever();
        await Task.Delay(Outage);
        _proxy.Restore();

        return await observing;
    }

    private static X509Certificate2 NewCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
    }

    private sealed class ScriptedAgent : IScriptService
    {
        private readonly TimeSpan _duration;
        private DateTime _started;
        private int _completeCalls;

        public ScriptedAgent(TimeSpan duration) => _duration = duration;

        public int CompleteCalls => _completeCalls;

        public ScriptStatusResponse StartScript(StartScriptCommand command)
        {
            _started = DateTime.UtcNow;

            return new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);
        }

        public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
        {
            var done = DateTime.UtcNow - _started >= _duration;
            var logs = done && request.LastLogSequence == 0 ? new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "script-output", DateTimeOffset.UtcNow) } : new List<ProcessOutput>();

            return new ScriptStatusResponse(request.Ticket, done ? ProcessState.Complete : ProcessState.Running, 0, logs, done ? 1 : 0);
        }

        public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
        {
            Interlocked.Increment(ref _completeCalls);

            return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), command.LastLogSequence);
        }

        public ScriptStatusResponse CancelScript(CancelScriptCommand command)
            => new(command.Ticket, ProcessState.Complete, -2, new List<ProcessOutput>(), 0);
    }

    private sealed class AsyncAdapter : IScriptServiceAsync
    {
        private readonly IScriptService _inner;

        public AsyncAdapter(IScriptService inner) => _inner = inner;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command, CancellationToken ct) => Task.FromResult(_inner.StartScript(command));
        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct) => Task.FromResult(_inner.GetStatus(request));
        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct) => Task.FromResult(_inner.CompleteScript(command));
        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct) => Task.FromResult(_inner.CancelScript(command));
    }

    /// <summary>Loopback TCP forwarder that can be severed (kills live connections, refuses new ones) and restored.</summary>
    private sealed class ToggleProxy : IDisposable
    {
        private readonly int _targetPort;
        private readonly object _gate = new();
        private readonly List<TcpClient> _live = new();
        private TcpListener _listener;
        private bool _disposed;

        public ToggleProxy(int targetPort)
        {
            _targetPort = targetPort;
            Port = FreePort();
            Listen();
        }

        public int Port { get; }

        public void Sever()
        {
            lock (_gate)
            {
                _listener?.Stop();
                _listener = null;
                foreach (var c in _live) c.Dispose();
                _live.Clear();
            }
        }

        public void Restore() => Listen();

        public void Dispose()
        {
            _disposed = true;
            Sever();
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

            return port;
        }

        private void Listen()
        {
            TcpListener listener;

            lock (_gate)
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                _listener = listener;
            }

            _ = Task.Run(() => AcceptLoopAsync(listener));
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (!_disposed)
            {
                TcpClient inbound;
                try { inbound = await listener.AcceptTcpClientAsync(); }
                catch { return; }

                _ = Task.Run(() => PipeAsync(inbound));
            }
        }

        private async Task PipeAsync(TcpClient inbound)
        {
            var outbound = new TcpClient();

            try
            {
                await outbound.ConnectAsync(IPAddress.Loopback, _targetPort);
                lock (_gate) { _live.Add(inbound); _live.Add(outbound); }

                await Task.WhenAny(inbound.GetStream().CopyToAsync(outbound.GetStream()), outbound.GetStream().CopyToAsync(inbound.GetStream()));
            }
            catch { /* severed */ }
            finally
            {
                inbound.Dispose();
                outbound.Dispose();
            }
        }
    }
}
