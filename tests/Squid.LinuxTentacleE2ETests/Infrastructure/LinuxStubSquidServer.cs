using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Squid.LinuxTentacleE2ETests.Infrastructure;

/// <summary>
/// Phase 12.M.L.C.1+ — slim in-process HTTP REST stub of the Squid
/// server's <c>register</c> endpoint, used by Linux register E2E tests.
///
/// <para><b>Why a slim Linux-specific stub vs sharing Windows project's
/// StubSquidServer</b>: the Windows project's full StubSquidServer
/// includes Halibut polling listener + cert generation (~600 lines)
/// — needed for the Windows upgrade-flow / dispatch tests but
/// overkill for register E2E. This slim version is ~120 lines and
/// only handles the register REST surface, keeping the Linux project
/// self-contained without cross-project coupling.</para>
///
/// <para><b>What it provides:</b>
/// <list type="bullet">
///   <item>HTTP listener on a random loopback port (no admin / TLS setup).</item>
///   <item>Routes <c>POST /api/machines/register/tentacle-listening</c> +
///         <c>POST /api/machines/register/tentacle-polling</c> to a
///         canned response with the configured <see cref="ServerThumbprint"/>.</item>
///   <item><see cref="ReceivedRegistrations"/> records every register
///         request body so tests can assert on payload shape (machineName,
///         roles, thumbprint, etc.).</item>
///   <item><see cref="ConfigureRegisterStatusCode"/> +
///         <see cref="ConfigureRegisterBody"/> let failure-path tests
///         inject 401 / 500 / malformed responses.</item>
/// </list></para>
///
/// <para><b>HTTP not HTTPS</b>: non-root binding + no cert ACL = simpler.
/// The production binary's <c>EnsureSchemeSafeForSecret</c> warns on
/// <c>http://</c> but proceeds (Rule 11 enforcement Warn mode by default),
/// so the stub works without SSL.</para>
/// </summary>
public sealed class LinuxStubSquidServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<RegisterRequestRecord> _received = new();
    private int _registerStatusCode = 200;
    private string _registerBodyOverride;

    /// <summary>Stub server thumbprint returned in register responses.</summary>
    public string ServerThumbprint { get; } = "STUB1234567890ABCDEF1234567890ABCDEF1234";

    /// <summary>Base URL operators pass via <c>--server</c>. Includes scheme + port + trailing slash.</summary>
    public Uri BaseUrl { get; }

    public IReadOnlyList<RegisterRequestRecord> ReceivedRegistrations => _received.ToArray();

    private LinuxStubSquidServer(HttpListener listener, Uri baseUrl)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public static LinuxStubSquidServer Start()
    {
        // Allocate a random loopback port via TcpListener(0), then close
        // before binding HttpListener (small race window acceptable for tests).
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var baseUrl = new Uri($"http://127.0.0.1:{port}/");
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl.ToString());
        listener.Start();

        return new LinuxStubSquidServer(listener, baseUrl);
    }

    /// <summary>Override the register status code (default: 200). Tests injecting 401 / 500 use this.</summary>
    public void ConfigureRegisterStatusCode(int statusCode) => _registerStatusCode = statusCode;

    /// <summary>Override the register response body (default: canonical success). Tests injecting malformed JSON / missing fields use this.</summary>
    public void ConfigureRegisterBody(string body) => _registerBodyOverride = body;

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Listener disposed mid-await — exit loop cleanly.
                return;
            }

            try { HandleRequest(ctx); }
            catch
            {
                // Don't let a per-request exception kill the loop;
                // best-effort response close.
                try { ctx.Response.StatusCode = 500; ctx.Response.OutputStream.Close(); } catch { }
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;

        // Both flavors hit /api/machines/register/<flavor> with the same
        // shape envelope. Record + respond to either.
        if (path.StartsWith("/api/machines/register/", StringComparison.OrdinalIgnoreCase)
            && ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleRegister(ctx, path);
            return;
        }

        // Anything else: 404. Tests that rely on a specific endpoint
        // surface this immediately rather than hanging.
        ctx.Response.StatusCode = 404;
        ctx.Response.OutputStream.Close();
    }

    private void HandleRegister(HttpListenerContext ctx, string path)
    {
        // Read the request body (UTF-8 JSON).
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        // Capture headers operators may want to assert on (X-API-KEY etc.).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ctx.Request.Headers.AllKeys)
            if (name != null) headers[name] = ctx.Request.Headers[name] ?? string.Empty;

        _received.Add(new RegisterRequestRecord(path, body, headers));

        // Build response (or use override).
        var responseBody = _registerBodyOverride ?? BuildSuccessResponseBody();
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);

        ctx.Response.StatusCode = _registerStatusCode;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = responseBytes.Length;
        ctx.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private string BuildSuccessResponseBody()
    {
        // Schema must match TentacleRegistrationClient.RegistrationResponse
        // (camelCase via JsonNamingPolicy.CamelCase): { data: { machineId,
        // serverThumbprint, subscriptionUri } }. MachineId must be > 0
        // for EnsureRegistrationPayloadComplete to accept.
        var payload = new
        {
            data = new
            {
                machineId = 12345,
                serverThumbprint = ServerThumbprint,
                subscriptionUri = $"poll://stub-{Guid.NewGuid():N}/"
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* best-effort */ }
        try { _listener.Stop(); } catch { /* best-effort */ }
        try { _acceptLoop.Wait(2_000); } catch { /* best-effort */ }
        try { _listener.Close(); } catch { /* best-effort */ }
        try { _cts.Dispose(); } catch { /* best-effort */ }
    }
}

/// <summary>
/// One captured register call. Used by tests to assert on the payload
/// the production binary actually sent.
/// </summary>
public sealed record RegisterRequestRecord(string Path, string Body, Dictionary<string, string> Headers);
