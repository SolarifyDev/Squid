using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Instance;
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
    new CheckServicesCommand(),
    new NewCertificateCommand(),
    new RegisterCommand(),
    new ServiceCommand(),
    new CreateInstanceCommand(),
    new ListInstancesCommand(),
    new DeleteInstanceCommand(),
    new VersionCommand()
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

    // Extract --instance NAME (the instance-aware commands need to know which config to load
    // and which certs dir to use). Remove it from the arg array before the rest of the pipeline
    // sees it, so ConfigurationBuilder.AddCommandLine doesn't choke on an unknown key.
    var (instanceName, argsAfterInstance) = InstanceSelector.ExtractInstanceArg(route.RemainingArgs);

    var configBuilder = new ConfigurationBuilder();

    // Priority (low → high): per-instance config.json → appsettings.json → env vars → CLI args.
    // This lets `register` persist to the config file and `systemd run` pick it up, while
    // still allowing env vars / CLI args to override for debugging or Docker-style launches.
    var instanceConfigPath = TryGetInstanceConfigPath(instanceName);
    if (instanceConfigPath != null)
        configBuilder.AddJsonFile(instanceConfigPath, optional: true, reloadOnChange: false);

    configBuilder.AddJsonFile("appsettings.json", optional: true);
    configBuilder.AddEnvironmentVariables();
    configBuilder.AddCommandLine(argsAfterInstance);

    var config = configBuilder.Build();

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

static string TryGetInstanceConfigPath(string instanceName)
{
    try
    {
        var record = InstanceSelector.Resolve(instanceName);
        return new TentacleConfigFile(record.ConfigPath).Exists() ? record.ConfigPath : null;
    }
    catch
    {
        // Instance lookup failures are non-fatal at startup — commands that actually need
        // an instance (register, run) will fail with a clear error later.
        return null;
    }
}

static void PrintHelp(ITentacleCommand[] commands)
{
    Console.WriteLine("Squid Tentacle — Deployment Agent");
    Console.WriteLine();
    Console.WriteLine("Usage: squid-tentacle <command> [--instance NAME] [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");

    foreach (var cmd in commands)
        Console.WriteLine($"  {cmd.Name,-20} {cmd.Description}");

    Console.WriteLine($"  {"help",-20} Show this help message");
    Console.WriteLine();
    Console.WriteLine("Instance management (multiple Tentacles on one host):");
    Console.WriteLine("  squid-tentacle create-instance --instance production");
    Console.WriteLine("  squid-tentacle list-instances");
    Console.WriteLine("  squid-tentacle delete-instance --instance production");
    Console.WriteLine();
    Console.WriteLine("Install + register + run (typical flow):");
    Console.WriteLine("  squid-tentacle register --server URL --api-key KEY --role R --environment E");
    Console.WriteLine("  sudo squid-tentacle service install");
    Console.WriteLine();
    Console.WriteLine("Configuration sources (low → high): instance config → appsettings.json → env (Tentacle__Key) → CLI (--Tentacle:Key=V)");
}
