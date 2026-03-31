using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiScriptContextPreparer(
    IKubernetesApiContextScriptBuilder builder,
    ILocalProcessRunner processRunner) : IScriptContextPreparer
{
    private const string EnvVarPrefix = "SQUID_";

    private static readonly Dictionary<string, string> EnvVarMapping = new(StringComparer.Ordinal)
    {
        ["SQUID_KUBECONFIG"] = "KUBECONFIG",
        ["SQUID_HTTPS_PROXY"] = "HTTPS_PROXY",
        ["SQUID_HTTP_PROXY"] = "HTTP_PROXY",
        ["SQUID_NO_PROXY"] = "NO_PROXY",
        ["SQUID_AZURE_CONFIG_DIR"] = "AZURE_CONFIG_DIR"
    };

    public async Task<ScriptContextResult> PrepareAsync(string script, ScriptContext context, string workDir, CancellationToken ct)
    {
        var customKubectl = ResolveCustomKubectl(context);

        if (ScriptSyntaxHelper.IsShellSyntax(context?.Syntax ?? ScriptSyntax.Bash))
            return PrepareForShellSyntax(script, context, customKubectl);

        return await PrepareForNonShellSyntaxAsync(script, context, workDir, customKubectl, ct).ConfigureAwait(false);
    }

    private ScriptContextResult PrepareForShellSyntax(string script, ScriptContext context, string customKubectl)
    {
        var wrapped = builder.WrapWithContext(script, context, customKubectl);

        return new ScriptContextResult { Script = wrapped };
    }

    private async Task<ScriptContextResult> PrepareForNonShellSyntaxAsync(string script, ScriptContext context, string workDir, string customKubectl, CancellationToken ct)
    {
        var setupScript = builder.BuildSetupScript(context, customKubectl);
        var setupPath = Path.Combine(workDir, "kubectl-context-setup.sh");
        await File.WriteAllTextAsync(setupPath, setupScript, ct).ConfigureAwait(false);

        var result = await processRunner.RunAsync("bash", $"\"{setupPath}\"", workDir, ct).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"Kubectl context setup failed (exit code {result.ExitCode}): {string.Join('\n', result.StderrLines)}");

        var envVars = ParseEnvironmentVariables(result.LogLines);

        return new ScriptContextResult { Script = script, EnvironmentVariables = envVars };
    }

    internal static Dictionary<string, string> ParseEnvironmentVariables(List<string> logLines)
    {
        var envVars = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in logLines)
        {
            if (!line.StartsWith(EnvVarPrefix, StringComparison.Ordinal)) continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = line[..eqIndex];
            var value = line[(eqIndex + 1)..];

            if (EnvVarMapping.TryGetValue(key, out var mappedKey))
                envVars[mappedKey] = value;
        }

        return envVars;
    }

    private static string ResolveCustomKubectl(ScriptContext context)
    {
        return context?.Variables?
            .FirstOrDefault(v => string.Equals(v.Name, SpecialVariables.Kubernetes.CustomKubectlExecutable, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }
}
