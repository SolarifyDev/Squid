using System.Text;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public class LocalProcessExecutionStrategy : IExecutionStrategy
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly ILocalProcessRunner _processRunner;
    private readonly IScriptContextWrapper? _scriptContextWrapper;

    public LocalProcessExecutionStrategy(
        ICalamariPayloadBuilder payloadBuilder,
        ILocalProcessRunner processRunner,
        IScriptContextWrapper? scriptContextWrapper = null)
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
        var syntax = request.Syntax;

        var payload = _payloadBuilder.Build(request, syntax);

        var packagePath = Path.Combine(workDir, payload.PackageFileName);
        var variablePath = Path.Combine(workDir, "variables.json");
        var sensitivePath = Path.Combine(workDir, "sensitiveVariables.json");

        await File.WriteAllBytesAsync(packagePath, payload.PackageBytes, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(variablePath, payload.VariableBytes, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(sensitivePath, payload.SensitiveBytes, ct).ConfigureAwait(false);

        var scriptBody = payload.FillTemplate(packagePath, variablePath, sensitivePath);
        scriptBody = ApplyContextPreparationIfRequired(request, scriptBody);

        var (executable, args, scriptFileName) = BuildCalamariInvocation(syntax);
        var scriptPath = Path.Combine(workDir, scriptFileName);
        await File.WriteAllTextAsync(scriptPath, scriptBody, Utf8NoBom, ct).ConfigureAwait(false);

        Log.Information("Executing packaged YAML deployment locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(executable, args.Replace("{{ScriptPath}}", scriptPath, StringComparison.Ordinal), workDir, ct, request.Timeout, request.Masker).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteScriptLocallyAsync(
        DirectScriptExecutionPlan plan, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(plan.Files, workDir);

        var (executable, arguments, scriptFileName) = BuildDirectScriptInvocation(plan);
        var scriptPath = Path.Combine(workDir, scriptFileName);
        await File.WriteAllTextAsync(scriptPath, plan.ScriptBody, Utf8NoBom, ct).ConfigureAwait(false);

        Log.Information("Executing script locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(executable, arguments.Replace("{{ScriptPath}}", scriptPath, StringComparison.Ordinal), workDir, ct, plan.Request.Timeout, plan.Request.Masker)
            .ConfigureAwait(false);
    }

    private static void WriteFilesToDirectory(Dictionary<string, byte[]> files, string workDir)
    {
        if (files == null) return;

        var normalizedWorkDir = Path.GetFullPath(workDir);

        foreach (var file in files)
        {
            var filePath = Path.GetFullPath(Path.Combine(workDir, file.Key));

            if (!filePath.StartsWith(normalizedWorkDir, StringComparison.Ordinal))
                throw new InvalidOperationException($"Path traversal detected: file key '{file.Key}' resolves outside work directory.");

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

    private static (string Executable, string Arguments, string ScriptFileName) BuildCalamariInvocation(ScriptSyntax syntax)
    {
        return syntax switch
        {
            ScriptSyntax.Bash => ("bash", "\"{{ScriptPath}}\"", "calamari-deploy.sh"),
            ScriptSyntax.PowerShell => ("pwsh", "-NoProfile -NonInteractive -File \"{{ScriptPath}}\"", "calamari-deploy.ps1"),
            _ => throw new InvalidOperationException($"Unsupported script syntax '{syntax}' for Calamari deployment.")
        };
    }

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

        var scriptContext = new ScriptContext
        {
            Endpoint = request.EndpointContext,
            Syntax = request.Syntax,
            Variables = request.Variables,
            ActionProperties = request.ActionProperties
        };

        return _scriptContextWrapper.WrapScript(scriptBody, scriptContext);
    }
}
