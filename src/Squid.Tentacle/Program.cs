using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var commands = new ITentacleCommand[]
{
    new RunCommand(),
    new ShowThumbprintCommand(),
    new ShowConfigCommand(),
    new NewCertificateCommand(),
    new RegisterCommand(),
    new ServiceCommand()
};

try
{
    var route = CommandResolver.Resolve(commands, args);

    if (route.HelpRequested)
    {
        PrintHelp(commands);
        return 0;
    }

    if (route.UnknownCommand != null)
    {
        Console.Error.WriteLine($"Unknown command: {route.UnknownCommand}");
        PrintHelp(commands);
        return 1;
    }

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(route.RemainingArgs)
        .Build();

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    return await route.Command.ExecuteAsync(route.RemainingArgs, config, cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    Log.Information("Squid Tentacle shutdown requested");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Squid Tentacle terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static void PrintHelp(ITentacleCommand[] commands)
{
    Console.WriteLine("Squid Tentacle — Deployment Agent");
    Console.WriteLine();
    Console.WriteLine("Usage: squid-tentacle <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");

    foreach (var cmd in commands)
        Console.WriteLine($"  {cmd.Name,-20} {cmd.Description}");

    Console.WriteLine($"  {"help",-20} Show this help message");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  squid-tentacle run                           Start the agent (default)");
    Console.WriteLine("  squid-tentacle show-thumbprint               Display certificate thumbprint");
    Console.WriteLine("  squid-tentacle new-certificate               Generate certificate if missing");
    Console.WriteLine("  squid-tentacle register --server URL ...     Register with Squid Server");
    Console.WriteLine("  squid-tentacle service install               Install as systemd service");
    Console.WriteLine();
    Console.WriteLine("Configuration: --Tentacle:Key=Value, environment variables (Tentacle__Key), or appsettings.json");
}
