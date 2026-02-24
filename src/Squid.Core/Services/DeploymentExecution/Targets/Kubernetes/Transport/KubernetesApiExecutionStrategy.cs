using System.Text;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.ExecutionPlans;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiExecutionStrategy : IExecutionStrategy
{
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly ILocalProcessRunner _processRunner;
    private readonly KubernetesApiScriptContextWrapper? _scriptContextWrapper;

    public KubernetesApiExecutionStrategy(
        ICalamariPayloadBuilder payloadBuilder,
        ILocalProcessRunner processRunner,
        KubernetesApiScriptContextWrapper? scriptContextWrapper = null)
    {
        _payloadBuilder = payloadBuilder;
        _processRunner = processRunner;
        _scriptContextWrapper = scriptContextWrapper;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        var workDir = CreateWorkDirectory();
        var plan = ScriptExecutionPlanFactory.Create(request);

        try
        {
            if (plan is PackagedPayloadExecutionPlan packagedPlan)
                return await ExecuteCalamariLocallyAsync(packagedPlan, workDir, ct).ConfigureAwait(false);

            return await ExecuteScriptLocallyAsync((DirectScriptExecutionPlan)plan, workDir, ct).ConfigureAwait(false);
        }
        finally
        {
            CleanupWorkDirectory(workDir);
        }
    }

    private async Task<ScriptExecutionResult> ExecuteCalamariLocallyAsync(
        PackagedPayloadExecutionPlan plan, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(plan.Files, workDir);

        var request = plan.Request;

        var payload = _payloadBuilder.Build(request);

        var packagePath = Path.Combine(workDir, payload.PackageFileName);
        var variablePath = Path.Combine(workDir, "variables.json");
        var sensitivePath = Path.Combine(workDir, "sensitiveVariables.json");

        await File.WriteAllBytesAsync(packagePath, payload.PackageBytes, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(variablePath, payload.VariableBytes, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(sensitivePath, payload.SensitiveBytes, ct).ConfigureAwait(false);

        var scriptBody = payload.FillTemplate(packagePath, variablePath, sensitivePath);
        scriptBody = ApplyContextPreparationIfRequired(request, scriptBody);

        Log.Information("Executing packaged YAML deployment locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(
            "pwsh", $"-NoProfile -NonInteractive -Command \"{EscapeForCommandLine(scriptBody)}\"",
            workDir, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteScriptLocallyAsync(
        DirectScriptExecutionPlan plan, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(plan.Files, workDir);

        var (executable, arguments, scriptFileName) = BuildDirectScriptInvocation(plan);
        var scriptPath = Path.Combine(workDir, scriptFileName);
        await File.WriteAllTextAsync(scriptPath, plan.ScriptBody, Encoding.UTF8, ct).ConfigureAwait(false);

        Log.Information("Executing script locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(executable, arguments.Replace("{{ScriptPath}}", scriptPath, StringComparison.Ordinal), workDir, ct)
            .ConfigureAwait(false);
    }

    private static void WriteFilesToDirectory(Dictionary<string, byte[]> files, string workDir)
    {
        if (files == null) return;

        foreach (var file in files)
        {
            var filePath = Path.Combine(workDir, file.Key);
            var dir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(filePath, file.Value);
        }
    }

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squid-exec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupWorkDirectory(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up work directory {WorkDir}", workDir);
        }
    }

    private static string EscapeForCommandLine(string script)
        => script.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static (string Executable, string Arguments, string ScriptFileName) BuildDirectScriptInvocation(
        DirectScriptExecutionPlan plan)
    {
        return plan.Request.Syntax switch
        {
            ScriptSyntax.Bash => ("bash", "\"{{ScriptPath}}\"", "script.sh"),
            ScriptSyntax.PowerShell => ("pwsh", "-NoProfile -NonInteractive -File \"{{ScriptPath}}\"", "script.ps1"),
            _ => throw new InvalidOperationException($"Unsupported script syntax '{plan.Request.Syntax}'.")
        };
    }

    private string ApplyContextPreparationIfRequired(ScriptExecutionRequest request, string scriptBody)
    {
        if (_scriptContextWrapper == null)
            return scriptBody;

        if (request.ResolveContextPreparationPolicy() != ContextPreparationPolicy.Apply)
            return scriptBody;

        return _scriptContextWrapper.WrapScript(
            scriptBody,
            request.EndpointJson,
            request.Account,
            request.Syntax,
            request.Variables);
    }
}
