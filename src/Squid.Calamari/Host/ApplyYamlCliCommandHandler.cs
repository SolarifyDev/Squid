using Squid.Calamari.Commands;

namespace Squid.Calamari.Host;

public sealed class ApplyYamlCliCommandHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "apply-yaml",
        "apply-yaml  --file=<path> [--variables=<path>] [--sensitive=<path>] [--password=<pw>] [--namespace=<ns>]",
        "Render raw YAML with variables and apply via kubectl.");

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (CommandLineArguments.ContainsHelpToken(args))
        {
            UsagePrinter.PrintCommand(Descriptor, Console.Out);
            return 0;
        }

        var parsed = CommandLineArguments.ParseKeyValueArgs(args);

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
        var result = await command.ExecuteWithResultAsync(yamlFile, variablesPath, sensitivePath, password, @namespace, ct)
            .ConfigureAwait(false);

        return result.ExitCode;
    }
}
