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
    public static async Task RunAsync(string[] args, IConfiguration configuration, EmbeddedServiceHost embedded)
    {
        var token = SetupToken.LoadOrCreate(new DataDirectorySetting(configuration));
        var session = new SetupSession(configuration, embedded, token);

        await using var app = Build(args, configuration, session);

        await app.StartAsync().ConfigureAwait(false);

        AnnounceSetupUrl(app, token);

        await app.WaitForShutdownAsync().ConfigureAwait(false);

        EnsureCompleted(session);

        token.Delete();
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

    private static void AnnounceSetupUrl(WebApplication app, SetupToken token)
    {
        var baseUrl = ToBrowsableUrl(app.Urls.FirstOrDefault() ?? "http://localhost:8080");

        Log.Warning("Squid is not set up yet. Open {SetupUrl} to finish the setup (the token is also in {TokenFile})", $"{baseUrl}/setup?token={token.Value}", token.FilePath);
    }

    /// <summary>Wildcard listen addresses (<c>[::]</c>, <c>0.0.0.0</c>, <c>+</c>, <c>*</c>) become <c>localhost</c>.</summary>
    private static string ToBrowsableUrl(string listenUrl) => Regex.Replace(listenUrl, @"://(\[::\]|0\.0\.0\.0|\+|\*)", "://localhost");

    private static void EnsureCompleted(SetupSession session)
    {
        if (!session.Completed.IsCompleted)
            throw new OperationCanceledException("Squid was stopped before the first-run setup completed");
    }
}
