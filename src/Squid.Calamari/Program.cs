using Squid.Calamari.Commands;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var subcommand = args[0];
var ct = CancellationToken.None;

switch (subcommand)
{
    case "run-script":
        return await HandleRunScriptAsync(args[1..], ct);

    case "apply-yaml":
        return await HandleApplyYamlAsync(args[1..], ct);

    default:
        Console.Error.WriteLine($"Unknown subcommand: {subcommand}");
        PrintUsage();
        return 1;
}

static async Task<int> HandleRunScriptAsync(string[] args, CancellationToken ct)
{
    var parsed = ParseArgs(args);

    if (!parsed.TryGetValue("--script", out var scriptPath) || string.IsNullOrEmpty(scriptPath))
    {
        Console.Error.WriteLine("run-script requires --script=<path>");
        return 1;
    }

    parsed.TryGetValue("--variables", out var variablesPath);
    parsed.TryGetValue("--sensitive", out var sensitivePath);
    parsed.TryGetValue("--password", out var password);

    variablesPath ??= Path.Combine(Path.GetDirectoryName(scriptPath) ?? ".", "variables.json");

    var command = new RunScriptCommand();

    return await command.ExecuteAsync(scriptPath, variablesPath, sensitivePath, password, ct)
        .ConfigureAwait(false);
}

static async Task<int> HandleApplyYamlAsync(string[] args, CancellationToken ct)
{
    var parsed = ParseArgs(args);

    if (!parsed.TryGetValue("--file", out var yamlFile) || string.IsNullOrEmpty(yamlFile))
    {
        Console.Error.WriteLine("apply-yaml requires --file=<path>");
        return 1;
    }

    parsed.TryGetValue("--variables", out var variablesPath);
    parsed.TryGetValue("--sensitive", out var sensitivePath);
    parsed.TryGetValue("--password", out var password);
    parsed.TryGetValue("--namespace", out var @namespace);

    variablesPath ??= Path.Combine(Path.GetDirectoryName(yamlFile) ?? ".", "variables.json");

    var command = new ApplyYamlCommand();

    return await command.ExecuteAsync(yamlFile, variablesPath, sensitivePath, password, @namespace, ct)
        .ConfigureAwait(false);
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var arg in args)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            continue;

        var eqIndex = arg.IndexOf('=', StringComparison.Ordinal);

        if (eqIndex < 0)
            continue;

        var key = arg[..eqIndex];
        var value = arg[(eqIndex + 1)..];
        result[key] = value;
    }

    return result;
}

static void PrintUsage()
{
    Console.WriteLine("squid-calamari <subcommand> [options]");
    Console.WriteLine();
    Console.WriteLine("Subcommands:");
    Console.WriteLine("  run-script  --script=<path> [--variables=<path>] [--sensitive=<path>] [--password=<pw>]");
    Console.WriteLine("  apply-yaml  --file=<path> [--variables=<path>] [--sensitive=<path>] [--password=<pw>] [--namespace=<ns>]");
}
