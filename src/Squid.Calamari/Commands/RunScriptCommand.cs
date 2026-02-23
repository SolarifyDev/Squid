using Squid.Calamari.Execution;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands;

/// <summary>
/// Handles the `run-script` subcommand.
/// Loads variables, prepends them as bash exports, then executes the script.
/// </summary>
public class RunScriptCommand
{
    public async Task<int> ExecuteAsync(
        string scriptPath,
        string variablesPath,
        string? sensitivePath,
        string? password,
        CancellationToken ct)
    {
        var workDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))
                      ?? Directory.GetCurrentDirectory();

        var variables = VariableFileLoader.MergeAll(variablesPath, sensitivePath, password);
        var bootstrappedScriptPath = WriteBootstrappedScript(workDir, scriptPath, variables);

        var outputProcessor = new ScriptOutputProcessor();
        var executor = new BashScriptExecutor();

        return await executor.ExecuteAsync(bootstrappedScriptPath, workDir, outputProcessor, ct)
            .ConfigureAwait(false);
    }

    private static string WriteBootstrappedScript(
        string workDir, string originalScriptPath, IDictionary<string, string> variables)
    {
        var originalScript = File.ReadAllText(originalScriptPath);
        var preamble = VariableBootstrapper.GeneratePreamble(variables);
        var bootstrappedScript = preamble + originalScript;

        var bootstrappedPath = Path.Combine(workDir, ".bootstrapped-script.sh");
        File.WriteAllText(bootstrappedPath, bootstrappedScript);

        return bootstrappedPath;
    }
}
