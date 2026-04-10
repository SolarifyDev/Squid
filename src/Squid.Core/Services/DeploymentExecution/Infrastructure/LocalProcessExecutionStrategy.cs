using System.Text;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public class LocalProcessExecutionStrategy : IExecutionStrategy
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly ILocalProcessRunner _processRunner;
    private readonly IScriptContextWrapper? _scriptContextWrapper;
    private readonly IScriptContextPreparer? _contextPreparer;

    public LocalProcessExecutionStrategy(
        ICalamariPayloadBuilder payloadBuilder,
        ILocalProcessRunner processRunner,
        IScriptContextWrapper? scriptContextWrapper = null,
        IScriptContextPreparer? contextPreparer = null)
    {
        _payloadBuilder = payloadBuilder;
        _processRunner = processRunner;
        _scriptContextWrapper = scriptContextWrapper;
        _contextPreparer = contextPreparer;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        string workDir = null;

        try
        {
            workDir = CreateWorkDirectory();
            var plan = ScriptExecutionPlanFactory.Create(request);

            if (plan is PackagedPayloadExecutionPlan packagedPlan)
                return await ExecuteCalamariLocallyAsync(packagedPlan, workDir, ct).ConfigureAwait(false);

            return await ExecuteScriptLocallyAsync((DirectScriptExecutionPlan)plan, workDir, ct).ConfigureAwait(false);
        }
        finally
        {
            if (workDir != null)
                CleanupWorkDirectory(workDir);
        }
    }

    private async Task<ScriptExecutionResult> ExecuteCalamariLocallyAsync(
        PackagedPayloadExecutionPlan plan, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(plan.DeploymentFiles, workDir);

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
        scriptBody = ApplyContextWrappingIfRequired(request, scriptBody);

        var (executable, args, scriptFileName) = BuildCalamariInvocation(syntax);
        var scriptPath = Path.Combine(workDir, scriptFileName);
        await File.WriteAllTextAsync(scriptPath, scriptBody, Utf8NoBom, ct).ConfigureAwait(false);

        Log.Information("[Deploy] Executing packaged YAML deployment locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(executable, args.Replace("{{ScriptPath}}", scriptPath, StringComparison.Ordinal), workDir, ct, request.Timeout, request.Masker).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteScriptLocallyAsync(
        DirectScriptExecutionPlan plan, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(plan.DeploymentFiles, workDir);

        var scriptBody = plan.ScriptBody;
        Dictionary<string, string> envVars = null;

        if (ShouldPrepareContext(plan.Request))
        {
            var prepResult = await PrepareContextAsync(plan.Request, scriptBody, workDir, ct).ConfigureAwait(false);
            scriptBody = prepResult.Script;
            envVars = prepResult.EnvironmentVariables?.Count > 0 ? prepResult.EnvironmentVariables : null;
        }

        var (executable, arguments, scriptFileName) = BuildDirectScriptInvocation(plan);
        var scriptPath = Path.Combine(workDir, scriptFileName);
        await File.WriteAllTextAsync(scriptPath, scriptBody, Utf8NoBom, ct).ConfigureAwait(false);

        Log.Information("[Deploy] Executing script locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(executable, arguments.Replace("{{ScriptPath}}", scriptPath, StringComparison.Ordinal), workDir, ct, plan.Request.Timeout, plan.Request.Masker, envVars).ConfigureAwait(false);
    }

    private bool ShouldPrepareContext(ScriptExecutionRequest request)
    {
        if (_contextPreparer == null && _scriptContextWrapper == null) return false;

        return request.ResolveContextPreparationPolicy() == ContextPreparationPolicy.Apply;
    }

    private async Task<ScriptContextResult> PrepareContextAsync(ScriptExecutionRequest request, string scriptBody, string workDir, CancellationToken ct)
    {
        var scriptContext = BuildScriptContext(request);

        if (_contextPreparer != null)
            return await _contextPreparer.PrepareAsync(scriptBody, scriptContext, workDir, ct).ConfigureAwait(false);

        if (_scriptContextWrapper != null)
        {
            var wrapped = _scriptContextWrapper.WrapScript(scriptBody, scriptContext);
            return new ScriptContextResult { Script = wrapped };
        }

        return new ScriptContextResult { Script = scriptBody };
    }

    private static ScriptContext BuildScriptContext(ScriptExecutionRequest request)
    {
        return new ScriptContext
        {
            Endpoint = request.EndpointContext,
            Syntax = request.Syntax,
            Variables = request.Variables,
            ActionProperties = request.ActionProperties
        };
    }

    private string ApplyContextWrappingIfRequired(ScriptExecutionRequest request, string scriptBody)
    {
        if (_scriptContextWrapper == null) return scriptBody;

        if (request.ResolveContextPreparationPolicy() != ContextPreparationPolicy.Apply)
            return scriptBody;

        var scriptContext = BuildScriptContext(request);

        return _scriptContextWrapper.WrapScript(scriptBody, scriptContext);
    }

    private static void WriteFilesToDirectory(DeploymentFileCollection files, string workDir)
    {
        if (files == null || files.Count == 0) return;

        var normalizedWorkDir = Path.GetFullPath(workDir);

        foreach (var file in files)
        {
            var filePath = Path.GetFullPath(Path.Combine(workDir, file.RelativePath));

            if (!filePath.StartsWith(normalizedWorkDir, StringComparison.Ordinal))
                throw new InvalidOperationException($"Path traversal detected: file '{file.RelativePath}' resolves outside work directory.");

            var dir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(filePath, file.Content);
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
            Log.Warning(ex, "[Deploy] Failed to clean up work directory {WorkDir}", workDir);
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
            ScriptSyntax.CSharp => ("dotnet-script", "\"{{ScriptPath}}\"", "script.csx"),
            ScriptSyntax.FSharp => ("dotnet", "fsi \"{{ScriptPath}}\"", "script.fsx"),
            ScriptSyntax.Python => ("python3", "\"{{ScriptPath}}\"", "script.py"),
            _ => throw new InvalidOperationException($"Unsupported script syntax '{plan.Request.Syntax}'.")
        };
    }
}
