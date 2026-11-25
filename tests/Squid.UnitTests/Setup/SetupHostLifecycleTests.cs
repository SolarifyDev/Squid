using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Squid.Api.Setup;
using Squid.Core.Setup.Embedded;
using Squid.Core.Settings.Setup;
using Squid.UnitTests.Support;

namespace Squid.UnitTests.Setup;

/// <summary>
/// Drives <see cref="SetupHost.RunAsync(string[], Microsoft.Extensions.Configuration.IConfiguration, SetupSession, Action{WebApplicationBuilder})"/>
/// end to end (real host on a loopback port, no database): what it logs while waiting for the wizard, and what it
/// leaves in the data directory afterwards.
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class SetupHostLifecycleTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly ConcurrentQueue<LogEvent> _events = new();
    private readonly ILogger _originalLogger = Log.Logger;

    public SetupHostLifecycleTests()
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new QueueSink(_events)).CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = _originalLogger;
        _dir.Dispose();
    }

    [Fact]
    public async Task WhileWaiting_TheTokenValueIsNeverLogged_OnlyWhereToReadIt()
    {
        var (session, host) = NewSession();

        await RunUntilStartedThenStop(session, host, markCompleted: true);

        var logged = string.Join("\n", _events.Select(e => e.RenderMessage()));

        logged.ShouldNotContain(session.Token.Value, Case.Sensitive, "the setup log is shipped to Seq; the token lets anyone who can read it finish the install");
        System.Text.RegularExpressions.Regex.IsMatch(logged, "token=[A-Za-z0-9]{32}").ShouldBeFalse("no ready-to-click link carrying a token");
        logged.ShouldContain(session.Token.FilePath, Case.Sensitive, "the operator must be told where the token is");
        logged.ShouldContain("/setup", Case.Sensitive);
    }

    [Fact]
    public async Task AfterASuccessfulSetup_TheTokenFileIsGone_AndTheNextLoadIssuesAFreshToken()
    {
        var (session, host) = NewSession();
        var original = session.Token.Value;

        File.Exists(session.Token.FilePath).ShouldBeTrue();

        await RunUntilStartedThenStop(session, host, markCompleted: true);

        File.Exists(session.Token.FilePath).ShouldBeFalse("a finished setup must not leave a usable token behind");
        SetupToken.LoadOrCreate(new DataDirectorySetting(host.Configuration)).Value.ShouldNotBe(original);
    }

    [Fact]
    public async Task WhenStoppedBeforeSetupCompletes_ItThrows_AndTheTokenSurvivesForTheRestart()
    {
        var (session, host) = NewSession();

        await Should.ThrowAsync<OperationCanceledException>(() => RunUntilStartedThenStop(session, host, markCompleted: false));

        File.Exists(session.Token.FilePath).ShouldBeTrue("the container restarts into the same wizard and the operator keeps the same token");
    }

    private (SetupSession Session, (Microsoft.Extensions.Configuration.IConfiguration Configuration, int Port) Host) NewSession()
    {
        var configuration = _dir.Configuration(new() { ["EmbeddedServices:Postgres:BinDirectory"] = Path.Combine(_dir.Path, "no-pg"), ["EmbeddedServices:Redis:ServerPath"] = Path.Combine(_dir.Path, "no-redis") });
        var session = new SetupSession(configuration, new EmbeddedServiceHost(new ProcessRunner(), _dir.Path), SetupToken.LoadOrCreate(new DataDirectorySetting(configuration)));

        return (session, (configuration, FreePort()));
    }

    [Theory]
    [InlineData("/setup?token={0}")]
    [InlineData("/api/setup/status?token={0}")]
    [InlineData("/setup?foo=bar&token={0}&x=1")]
    public async Task WhenTheOperatorOpensTheTokenLink_NoRequestLogLineContainsTheToken(string pathTemplate)
    {
        var (session, host) = NewSession();
        var path = string.Format(pathTemplate, session.Token.Value);

        await RunUntilStartedThenStop(session, host, markCompleted: true, requestPath: path);

        _events.Any(e => e.RenderMessage().Contains("/setup", StringComparison.Ordinal)).ShouldBeTrue("the announcement is still logged");

        foreach (var e in _events)
        {
            e.RenderMessage().ShouldNotContain(session.Token.Value, Case.Sensitive, "ASP.NET request logging would put 'Request starting ... ?token=...' into the log, which is shipped to Seq");
            e.Properties.Values.Select(v => v.ToString()).ShouldAllBe(v => !v.Contains(session.Token.Value, StringComparison.Ordinal), "no structured property may carry the token either");
        }
    }

    [Fact]
    public async Task ApplicationLogging_ThroughTheHostLoggerFactory_StillReachesTheProcessLogger()
    {
        var (session, host) = NewSession();

        await RunUntilStartedThenStop(session, host, markCompleted: true);

        _events.Any(e => e.RenderMessage().Contains(ApplicationLogMarker, StringComparison.Ordinal)).ShouldBeTrue("quieting ASP.NET request logging must not cut the host's logging off from the process sinks");
    }

    private const string ApplicationLogMarker = "setup-host-application-log-marker";

    [Fact]
    public async Task TheAnnouncedSetupUrl_IsCopyable_WithoutQuotesInsideTheUrl()
    {
        var (session, host) = NewSession();

        await RunUntilStartedThenStop(session, host, markCompleted: true);

        var announcement = _events.Select(e => e.RenderMessage()).Single(m => m.Contains("not set up yet", StringComparison.Ordinal));

        announcement.ShouldNotContain("\"http", Case.Sensitive, "a quoted URL cannot be copy-pasted");
        System.Text.RegularExpressions.Regex.IsMatch(announcement, @"Open http://127\.0\.0\.1:\d+/setup to finish").ShouldBeTrue(announcement);
    }

    private static Task RunUntilStartedThenStop(SetupSession session, (Microsoft.Extensions.Configuration.IConfiguration Configuration, int Port) host, bool markCompleted, string requestPath = null)
    {
        void Customize(WebApplicationBuilder b)
        {
            b.WebHost.UseUrls($"http://127.0.0.1:{host.Port}");
            b.Services.AddSingleton<IHostedService>(sp => new StopOnceStarted(sp.GetRequiredService<IHostApplicationLifetime>(), session, markCompleted, requestPath is null ? null : $"http://127.0.0.1:{host.Port}{requestPath}", sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StopOnceStarted>>()));
        }

        return SetupHost.RunAsync(Array.Empty<string>(), host.Configuration, session, Customize).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }

    private sealed class StopOnceStarted(IHostApplicationLifetime lifetime, SetupSession session, bool markCompleted, string requestUrl = null, Microsoft.Extensions.Logging.ILogger<StopOnceStarted> logger = null) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "{Marker}", ApplicationLogMarker);

            lifetime.ApplicationStarted.Register(() => _ = Task.Run(FinishAsync));

            return Task.CompletedTask;
        }

        private async Task FinishAsync()
        {
            try
            {
                if (requestUrl != null) await SendRequestAsync().ConfigureAwait(false);
            }
            finally
            {
                if (markCompleted) session.MarkCompleted();

                lifetime.StopApplication();
            }
        }

        private async Task SendRequestAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var response = await http.GetAsync(requestUrl).ConfigureAwait(false);

            await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
