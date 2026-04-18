using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Registration;

namespace Squid.Tentacle.Tests.Registration;

public class TentacleRegistrationClientTests
{
    [Fact]
    public void Constructor_AcceptsSettings()
    {
        var tentacleSettings = new TentacleSettings
        {
            ServerUrl = "https://test-server:7078",
            BearerToken = "test-token"
        };

        var client = new TentacleRegistrationClient(tentacleSettings, "/api/machines/register/kubernetes-agent");

        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_FailsAgainstNonListeningPort()
    {
        var tentacleSettings = new TentacleSettings
        {
            ServerUrl = "https://localhost:1",
            BearerToken = "test-token"
        };

        var client = new TentacleRegistrationClient(tentacleSettings, "/api/machines/register/kubernetes-agent");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<Exception>(
            () => client.RegisterAsync("sub-id", "thumbprint", cts.Token));
    }

    [Fact]
    public void RegistrationResult_HasRequiredProperties()
    {
        var result = new RegistrationResult
        {
            MachineId = 42,
            ServerThumbprint = "ABC123",
            SubscriptionUri = "poll://sub-id/"
        };

        result.MachineId.ShouldBe(42);
        result.ServerThumbprint.ShouldBe("ABC123");
        result.SubscriptionUri.ShouldBe("poll://sub-id/");
    }

    [Fact]
    public void RegistrationResult_Defaults()
    {
        var result = new RegistrationResult();

        result.MachineId.ShouldBe(0);
        result.ServerThumbprint.ShouldBe(string.Empty);
        result.SubscriptionUri.ShouldBe(string.Empty);
    }

    [Fact]
    public void TentacleSettings_DefaultValues()
    {
        var settings = new TentacleSettings();

        settings.Flavor.ShouldBe(string.Empty);
        settings.ServerUrl.ShouldBe("https://localhost:7078");
        settings.ServerCommsUrl.ShouldBe(string.Empty);
        settings.BearerToken.ShouldBe(string.Empty);
        settings.ApiKey.ShouldBe(string.Empty);
        settings.Roles.ShouldBe(string.Empty);
        settings.HealthCheckPort.ShouldBe(8080);
        settings.ListeningPort.ShouldBe(10933);
        settings.SubscriptionId.ShouldBe(string.Empty);
        settings.ChartRef.ShouldBe(TentacleSettings.DefaultKubernetesAgentChartRef);
    }

    [Fact]
    public void KubernetesSettings_DefaultValues()
    {
        var settings = new KubernetesSettings();

        settings.Namespace.ShouldBe("default");
        settings.UseScriptPods.ShouldBeFalse();
        settings.ScriptPodTimeoutSeconds.ShouldBe(1800);
        settings.ScriptPodCpuRequest.ShouldBe("25m");
        settings.ScriptPodMemoryRequest.ShouldBe("100Mi");
        settings.ScriptPodCpuLimit.ShouldBe("500m");
        settings.ScriptPodMemoryLimit.ShouldBe("512Mi");
    }

    // ========================================================================
    // Business-code-in-body validation — prevents the 2026-04-18 regression where
    // the server wrapped errors as HTTP 200 + body {"code":500}, and the client
    // mistakenly reported "Registration successful. MachineId=null".
    // ========================================================================

    [Fact]
    public async Task RegisterAsync_Http200WithBodyCode500_ThrowsWithServerMessage()
    {
        // Arrange: HTTP 200 envelope with logical failure inside body — exactly
        // the shape produced by the server's GlobalExceptionFilter for any
        // unmapped InvalidOperationException (e.g. legacy duplicate-name path).
        var client = BuildClientWithStubbedResponse(
            HttpStatusCode.OK,
            """{"code":500,"msg":"A machine named \"mars mac\" already exists in this space"}""");

        var ex = await Should.ThrowAsync<HttpRequestException>(() =>
            client.RegisterAsync("sub-1", "AABB", CancellationToken.None));

        ex.Message.ShouldContain("code 500");
        ex.Message.ShouldContain("A machine named");
    }

    [Fact]
    public async Task RegisterAsync_Http409FromServer_ThrowsAndPropagatesConflict()
    {
        // Server now maps MachineNameConflictException → 409 (see Squid.Api
        // GlobalExceptionFilter). Client must surface this distinctly so
        // operators don't misread it as "transient 5xx, retry".
        var client = BuildClientWithStubbedResponse(
            HttpStatusCode.Conflict,
            """{"code":409,"msg":"A machine named \"mars mac\" already exists in this space"}""");

        var ex = await Should.ThrowAsync<HttpRequestException>(() =>
            client.RegisterAsync("sub-1", "AABB", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ex.Message.ShouldContain("already exists");
    }

    [Fact]
    public async Task RegisterAsync_Http200WithEmptyData_ThrowsBecauseMachineIdMissing()
    {
        // Safeguard against "HTTP 200, body.code=200, but data is null/0".
        // Without this, the Tentacle would start polling with MachineId=0 and
        // the server would RST every Halibut handshake.
        var client = BuildClientWithStubbedResponse(
            HttpStatusCode.OK,
            """{"code":200,"msg":"Success","data":null}""");

        var ex = await Should.ThrowAsync<HttpRequestException>(() =>
            client.RegisterAsync("sub-1", "AABB", CancellationToken.None));

        ex.Message.ShouldContain("missing");
    }

    [Fact]
    public async Task RegisterAsync_Http200WithCompleteData_ReturnsResult()
    {
        var client = BuildClientWithStubbedResponse(
            HttpStatusCode.OK,
            """{"code":200,"msg":"Success","data":{"machineId":17,"serverThumbprint":"FAF04764","subscriptionUri":"poll://sub-1/"}}""");

        var result = await client.RegisterAsync("sub-1", "AABB", CancellationToken.None);

        result.MachineId.ShouldBe(17);
        result.ServerThumbprint.ShouldBe("FAF04764");
        result.SubscriptionUri.ShouldBe("poll://sub-1/");
    }

    [Fact]
    public async Task RegisterAsync_ClientErrorFromHttpLayer_NotRetried()
    {
        // 4xx responses are user errors (bad API key, bad payload) and the
        // retry loop should NOT retry them. Verify we throw immediately.
        var callCount = 0;
        var handler = new StubMessageHandler((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"code":401,"msg":"Unauthorized"}""", Encoding.UTF8, "application/json")
            });
        });

        var client = BuildClient(handler);

        await Should.ThrowAsync<HttpRequestException>(() =>
            client.RegisterAsync("sub-1", "AABB", CancellationToken.None));

        callCount.ShouldBe(1); // Retry loop gave up on the 4xx
    }

    private static TentacleRegistrationClient BuildClientWithStubbedResponse(HttpStatusCode status, string body)
    {
        var handler = new StubMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        return BuildClient(handler);
    }

    private static TentacleRegistrationClient BuildClient(StubMessageHandler handler)
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://unit-test-host",
            ApiKey = "test-key",
            MachineName = "mars mac",
            SpaceId = 1
        };

        // Fast retries so tests don't block the suite.
        var options = new TentacleRegistrationClientOptions
        {
            MaxRetries = 1,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            DelayAsync = (_, _) => Task.CompletedTask,
            HttpMessageHandlerFactory = () => handler
        };

        return new TentacleRegistrationClient(settings, "/api/machines/register/tentacle-polling", extraProperties: null, options);
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
