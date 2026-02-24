using Squid.Calamari.Host;

var registry = CalamariCommandRegistryFactory.CreateDefault();

if (args.Length == 0)
{
    PrintUsage(registry);
    return 1;
}

if (args.Length == 1 && CommandLineArguments.IsHelpToken(args[0]))
{
    PrintUsage(registry);
    return 0;
}

var subcommand = args[0];
var ct = CancellationToken.None;

if (!registry.TryGet(subcommand, out var handler) || handler is null)
{
    Console.Error.WriteLine($"Unknown subcommand: {subcommand}");
    PrintUsage(registry);
    return 1;
}

return await handler.ExecuteAsync(args[1..], ct).ConfigureAwait(false);

static void PrintUsage(CommandRegistry registry)
    => UsagePrinter.Print(registry, Console.Out);
