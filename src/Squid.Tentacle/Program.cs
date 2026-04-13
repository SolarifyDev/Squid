using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var (command, remainingArgs) = ResolveCommand(args);

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(remainingArgs)
        .Build();

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var exitCode = await command.ExecuteAsync(remainingArgs, config, cts.Token).ConfigureAwait(false);
    Environment.ExitCode = exitCode;
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

static (ITentacleCommand command, string[] remainingArgs) ResolveCommand(string[] args)
{
    var commands = new ITentacleCommand[]
    {
        new RunCommand(),
        new ShowThumbprintCommand(),
        new ShowConfigCommand(),
        new NewCertificateCommand(),
        new RegisterCommand(),
        new ServiceCommand()
    };

    if (args.Length == 0 || args[0].StartsWith("--") || args[0].StartsWith("-"))
        return (new RunCommand(), args);

    var verb = args[0].ToLowerInvariant();

    if (verb == "help" || verb == "--help" || verb == "-h")
    {
        PrintHelp(commands);
        Environment.Exit(0);
    }

    var matched = commands.FirstOrDefault(c => c.Name.Equals(verb, StringComparison.OrdinalIgnoreCase));

    if (matched != null)
        return (matched, args[1..]);

    Console.Error.WriteLine($"Unknown command: {verb}");
    PrintHelp(commands);
    Environment.Exit(1);
    return default;
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
