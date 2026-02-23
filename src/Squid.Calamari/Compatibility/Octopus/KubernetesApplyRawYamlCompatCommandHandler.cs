using Squid.Calamari.Commands;
using Squid.Calamari.Host;

namespace Squid.Calamari.Compatibility.Octopus;

/// <summary>
/// Minimal compatibility layer for Octopus Calamari's `kubernetes-apply-raw-yaml`.
/// This v1 maps a raw YAML file path passed via `--package` to Squid's `apply-yaml` flow.
/// Package archives (.zip/.nupkg) are not supported yet.
/// </summary>
public sealed class KubernetesApplyRawYamlCompatCommandHandler : ICommandHandler
{
    private readonly IOctopusRawYamlPackageResolver _packageResolver;

    public CommandDescriptor Descriptor { get; } = new(
        "kubernetes-apply-raw-yaml",
        "kubernetes-apply-raw-yaml  --package=<yaml-file> [--variables=<path>] [--sensitiveVariables=<path>] [--sensitiveVariablesPassword=<pw>] [--namespace=<ns>]",
        "Octopus-compat command that maps raw YAML apply arguments onto Squid apply-yaml.");

    public KubernetesApplyRawYamlCompatCommandHandler()
        : this(new OctopusRawYamlPackageResolver())
    {
    }

    public KubernetesApplyRawYamlCompatCommandHandler(IOctopusRawYamlPackageResolver packageResolver)
    {
        _packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var parsed = CommandLineArguments.ParseKeyValueArgs(args);

        if (!parsed.TryGetValue("--package", out var packagePath) || string.IsNullOrWhiteSpace(packagePath))
        {
            Console.Error.WriteLine("kubernetes-apply-raw-yaml requires --package=<yaml-file>");
            return 1;
        }

        parsed.TryGetValue("--variables", out var variablesPath);
        parsed.TryGetValue("--sensitiveVariables", out var sensitivePath);
        parsed.TryGetValue("--sensitiveVariablesPassword", out var password);
        parsed.TryGetValue("--namespace", out var @namespace);

        ResolvedRawYamlPackage? resolved = null;
        try
        {
            resolved = await _packageResolver.ResolveAsync(packagePath, ct).ConfigureAwait(false);

            variablesPath ??= Path.Combine(Path.GetDirectoryName(resolved.YamlFilePath) ?? ".", "variables.json");

            var command = new ApplyYamlCommand();
            var result = await command.ExecuteWithResultAsync(
                    resolved.YamlFilePath,
                    variablesPath,
                    sensitivePath,
                    password,
                    @namespace,
                    ct)
                .ConfigureAwait(false);

            return result.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (resolved is not null)
            {
                foreach (var path in resolved.CleanupPaths)
                {
                    try
                    {
                        if (Directory.Exists(path))
                            Directory.Delete(path, recursive: true);
                        else if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                        // Best-effort compat temp cleanup.
                    }
                }
            }
        }
    }
}
