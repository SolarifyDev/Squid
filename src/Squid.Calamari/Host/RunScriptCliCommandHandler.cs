using Squid.Calamari.Commands;

namespace Squid.Calamari.Host;

public sealed class RunScriptCliCommandHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "run-script",
        "run-script  --script=<path> [--variables=<path>] [--sensitive=<path>] [--password=<pw>]",
        "Run a bash script with bootstrapped variables.");

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (CommandLineArguments.ContainsHelpToken(args))
        {
            UsagePrinter.PrintCommand(Descriptor, Console.Out);
            return 0;
        }

        var parsed = CommandLineArguments.ParseKeyValueArgs(args);

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
        var result = await command.ExecuteWithResultAsync(scriptPath, variablesPath, sensitivePath, password, ct)
            .ConfigureAwait(false);

        return result.ExitCode;
    }
}
