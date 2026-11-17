using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// Transport failures that need their own recovery advice. An untrusted certificate (an on-prem Octopus behind an internal CA) never
/// heals by retrying, so it fails at once with its own kind; a transient DNS failure (EAI_AGAIN) heals, so it is retried like any
/// dropped connection, while a name that does not exist fails at once. The certificate case runs against a real TLS listener through
/// the production handler, so the exception chain the client classifies is the one the runtime really produces.
/// </summary>
public sealed class OctopusRestTransportFailureTests
{
    private const string ApiKey = "API-SECRETKEY0123456789";

    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task TlsHandshakeFailure_NotRetried_TlsKind()
    {
        var handler = new OctopusRestStubHandler(_ => throw new HttpRequestException("The SSL connection could not be established", new AuthenticationException("The remote certificate is invalid")));
        using var client = Client("https://octopus.example.com/", handler, new OctopusRestOptions { MaxRetries = 3 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Tls);
        handler.Requests.Count.ShouldBe(1, "a certificate the host does not trust is not trusted on the next attempt either");
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public void TlsMessage_TellsTheOperatorToTrustTheCertificate_NeverToRetryLater()
    {
        var message = OctopusRestException.MessageFor(OctopusRestErrorKind.Tls);

        message.ShouldContain("certificate");
        message.ShouldContain("trust store");
        message.ShouldNotContain("Try again later");
    }

    [Fact]
    public async Task ProductionHandler_UntrustedServerCertificate_TlsKindAfterOneHandshake()
    {
        using var certificate = SelfSignedCertificate();
        await using var server = TlsServer.Start(certificate);
        var options = new OctopusRestOptions { AllowLoopback = true, MaxRetries = 3 };
        using var client = new OctopusRestClient($"https://localhost:{server.Port}/", ApiKey, options, OctopusRestClientFactory.CreateHandler(options), RecordDelay);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Tls, customMessage: "a self-signed Octopus certificate must be reported as untrusted, not as an unreachable server");
        ex.InnerException.ShouldBeNull();
        server.Handshakes.ShouldBe(1, "the handshake is not retried");
        _delays.ShouldBeEmpty();
    }

    // EAI_AGAIN (the resolver could not answer now) heals by itself, so it is retried like a dropped connection; a name the resolver
    // says does not exist (a typo) fails at once.
    [Theory]
    [InlineData(SocketError.TryAgain, 3)]
    [InlineData(SocketError.HostNotFound, 1)]
    public async Task DnsResolutionFailure_RetriedOnlyWhenTransient(SocketError error, int expectedResolutions)
    {
        var resolutions = 0;
        var options = new OctopusRestOptions { MaxRetries = 2 };
        using var handler = OctopusRestClientFactory.CreateHandler(options, (_, _) =>
        {
            Interlocked.Increment(ref resolutions);
            throw new SocketException((int)error);
        });
        using var client = new OctopusRestClient("https://octopus.example.com/", ApiKey, options, handler, RecordDelay);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        resolutions.ShouldBe(expectedResolutions);
    }

    [Fact]
    public async Task DnsResolutionFailingOnce_RetriedAndConnects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolutions = 0;
        var options = new OctopusRestOptions { AllowLoopback = true, MaxRetries = 2 };

        using var handler = OctopusRestClientFactory.CreateHandler(options, (_, _) => Interlocked.Increment(ref resolutions) == 1 ? throw new SocketException((int)SocketError.TryAgain) : Task.FromResult(new[] { IPAddress.Loopback }));
        using var client = new OctopusRestClient($"http://octopus.example.com:{port}/", ApiKey, options, handler, RecordDelay);
        var serving = ServeOneRootAsync(listener);

        (await client.GetRootAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30))).IsOctopus.ShouldBeTrue();

        await serving;
        resolutions.ShouldBe(2);
        _delays.Count.ShouldBe(1);
    }

    private Task RecordDelay(TimeSpan delay, CancellationToken _)
    {
        lock (_delays) _delays.Add(delay);

        return Task.CompletedTask;
    }

    private OctopusRestClient Client(string serverUrl, OctopusRestStubHandler handler, OctopusRestOptions options) => new(serverUrl, ApiKey, options, handler, RecordDelay);

    private static async Task ServeOneRootAsync(TcpListener listener)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        var buffer = new byte[8192];
        var received = new System.Text.StringBuilder();

        while (!received.ToString().Contains("\r\n\r\n"))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return;
            received.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
        }

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(OctopusRestStubHandler.Root());
        var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    private static X509Certificate2 SelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));

        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly Task _accepting;
        private int _handshakes;

        private TlsServer(TcpListener listener, X509Certificate2 certificate)
        {
            _listener = listener;
            _certificate = certificate;
            _accepting = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Handshakes => Volatile.Read(ref _handshakes);

        public static TlsServer Start(X509Certificate2 certificate)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            return new TlsServer(listener, certificate);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var connection = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandshakeAsync(connection));
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task HandshakeAsync(TcpClient connection)
        {
            Interlocked.Increment(ref _handshakes);

            try
            {
                await using var ssl = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate });
            }
            catch
            {
                // The client rejects the certificate and drops the connection.
            }
            finally
            {
                connection.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _accepting;
        }
    }
}
