using Squid.Calamari.Commands.Package;

namespace Squid.Calamari.Host;

public sealed class DeployPackageCliCommandHandler : ICommandHandler
{
    public const string SensitivePasswordEnvVar = "SQUID_CALAMARI_SENSITIVE_PASSWORD";

    public CommandDescriptor Descriptor { get; } = new(
        "deploy-package",
        "deploy-package --archive=<path> [--hash=<sha256>] [--mode=<Versioned|Custom>] [--final-dir=<path>] [--variables=<path>] [--sensitive=<path>] [--password=<pw>]",
        "Deploy a package archive into a durable installation directory and run conventions.");

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (CommandLineArguments.ContainsHelpToken(args))
        {
            UsagePrinter.PrintCommand(Descriptor, Console.Out);
            return 0;
        }

        var parsed = CommandLineArguments.ParseKeyValueArgs(args);
        parsed.TryGetValue("--archive", out var archivePath);
        parsed.TryGetValue("--hash", out var hash);
        parsed.TryGetValue("--mode", out var mode);
        parsed.TryGetValue("--final-dir", out var finalDir);
        parsed.TryGetValue("--variables", out var variablesPath);
        parsed.TryGetValue("--sensitive", out var sensitivePath);
        parsed.TryGetValue("--password", out var argvPassword);

        var password = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SensitivePasswordEnvVar))
            ? Environment.GetEnvironmentVariable(SensitivePasswordEnvVar)
            : argvPassword;

        try
        {
            var command = new DeployPackageCommand();
            return await command.ExecuteAsync(archivePath ?? string.Empty, variablesPath, sensitivePath, password, hash, mode, finalDir, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
