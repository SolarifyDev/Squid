using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var tentacleSettings = TentacleApp.LoadTentacleSettings(config);
    var kubernetesSettings = TentacleApp.LoadKubernetesSettings(config);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var app = new TentacleApp();
    await app.RunAsync(tentacleSettings, kubernetesSettings, cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    Log.Information("Squid Tentacle shutdown requested");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Squid Tentacle terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
