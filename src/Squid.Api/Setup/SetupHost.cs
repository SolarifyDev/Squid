using System.Text.RegularExpressions;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup.Embedded;

namespace Squid.Api.Setup;

/// <summary>
/// A small web host that only exists while a self-contained install has no database: it serves the
/// web UI's setup wizard and <c>/api/setup/*</c> on the normal port, and returns once the wizard's
/// install has completed so the caller can start the real API host in the same process.
/// </summary>
public static class SetupHost
{
    public static Task RunAsync(string[] args, IConfiguration configuration, EmbeddedServiceHost embedded)
    {
        var token = SetupToken.LoadOrCreate(new DataDirectorySetting(configuration));

        return RunAsync(args, configuration, new SetupSession(configuration, embedded, token));
    }

    public static async Task RunAsync(string[] args, IConfiguration configuration, SetupSession session, Action<WebApplicationBuilder> customize = null)
    {
        await using var app = Build(args, configuration, session, customize: customize);

        await app.StartAsync().ConfigureAwait(false);

        AnnounceSetupUrl(app, session.Token);

        await app.WaitForShutdownAsync().ConfigureAwait(false);

        EnsureCompleted(session);

        session.Token.Delete();
    }

    public static WebApplication Build(string[] args, IConfiguration configuration, SetupSession session, string contentRoot = null, Action<WebApplicationBuilder> customize = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot ?? AppContext.BaseDirectory });

        builder.Configuration.AddConfiguration(configuration);
        builder.Host.UseSerilog();
        customize?.Invoke(builder);

        var app = builder.Build();

        app.UseBundledSpaFiles(app.Environment);
        app.MapSetupEndpoints(session);
        app.MapBundledSpaFallback(app.Environment);

        return app;
    }

    /// <summary>
    /// The token is deliberately NOT logged (logs are shipped to Seq / read by more people than the data volume): anyone who
    /// could read it could finish the install. It lives in an owner-only file, so the log only says where to read it.
    /// </summary>
    private static void AnnounceSetupUrl(WebApplication app, SetupToken token)
    {
        var baseUrl = ToBrowsableUrl(app.Urls.FirstOrDefault() ?? "http://localhost:8080");

        Log.Warning("Squid is not set up yet. Open {SetupUrl} to finish the setup. The setup token is not logged: read it from {TokenFile} (in Docker: docker exec <container> cat {TokenFile}) and open {SetupUrl}?token=<token>", $"{baseUrl}/setup", token.FilePath);
    }

    /// <summary>Wildcard listen addresses (<c>[::]</c>, <c>0.0.0.0</c>, <c>+</c>, <c>*</c>) become <c>localhost</c>.</summary>
    private static string ToBrowsableUrl(string listenUrl) => Regex.Replace(listenUrl, @"://(\[::\]|0\.0\.0\.0|\+|\*)", "://localhost");

    private static void EnsureCompleted(SetupSession session)
    {
        if (!session.Completed.IsCompleted)
            throw new OperationCanceledException("Squid was stopped before the first-run setup completed");
    }
}
