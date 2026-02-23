using Squid.Calamari.Execution;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands;

/// <summary>
/// Handles the `apply-yaml` subcommand.
/// Loads variables, substitutes #{Variable} tokens in the YAML, then runs kubectl apply.
/// </summary>
public class ApplyYamlCommand
{
    public async Task<int> ExecuteAsync(
        string yamlFile,
        string variablesPath,
        string? sensitivePath,
        string? password,
        string? @namespace,
        CancellationToken ct)
    {
        var workDir = Path.GetDirectoryName(Path.GetFullPath(yamlFile))
                      ?? Directory.GetCurrentDirectory();

        var variables = VariableFileLoader.MergeAll(variablesPath, sensitivePath, password);
        var expandedYamlPath = WriteExpandedYaml(workDir, yamlFile, variables);

        var nsArg = string.IsNullOrEmpty(@namespace) ? string.Empty : $" --namespace={@namespace}";
        var scriptBody = $"kubectl apply -f \"{expandedYamlPath}\"{nsArg}";
        var applyScriptPath = Path.Combine(workDir, ".apply-yaml.sh");
        File.WriteAllText(applyScriptPath, $"#!/usr/bin/env bash\nset -e\n{scriptBody}\n");

        var outputProcessor = new ScriptOutputProcessor();
        var executor = new BashScriptExecutor();

        return await executor.ExecuteAsync(applyScriptPath, workDir, outputProcessor, ct)
            .ConfigureAwait(false);
    }

    private static string WriteExpandedYaml(
        string workDir, string yamlFile, IDictionary<string, string> variables)
    {
        var yaml = File.ReadAllText(yamlFile);

        foreach (var (name, value) in variables)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            yaml = yaml.Replace($"#{{{name}}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        var expandedPath = Path.Combine(workDir, ".expanded-" + Path.GetFileName(yamlFile));
        File.WriteAllText(expandedPath, yaml);

        return expandedPath;
    }
}
