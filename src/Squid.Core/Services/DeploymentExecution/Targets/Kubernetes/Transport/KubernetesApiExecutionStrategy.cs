using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiExecutionStrategy : IExecutionStrategy
{
    private readonly ICalamariPayloadBuilder _payloadBuilder;
    private readonly ILocalProcessRunner _processRunner;

    public KubernetesApiExecutionStrategy(
        ICalamariPayloadBuilder payloadBuilder,
        ILocalProcessRunner processRunner)
    {
        _payloadBuilder = payloadBuilder;
        _processRunner = processRunner;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        var workDir = CreateWorkDirectory();

        try
        {
            if (request.CalamariCommand != null)
                return await ExecuteCalamariLocallyAsync(request, workDir, ct).ConfigureAwait(false);

            return await ExecuteScriptLocallyAsync(request, workDir, ct).ConfigureAwait(false);
        }
        finally
        {
            CleanupWorkDirectory(workDir);
        }
    }

    private async Task<ScriptExecutionResult> ExecuteCalamariLocallyAsync(
        ScriptExecutionRequest request, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(request.Files, workDir);

        var payload = _payloadBuilder.Build(request);

        var packagePath = Path.Combine(workDir, payload.PackageFileName);
        var variablePath = Path.Combine(workDir, "variables.json");
        var sensitivePath = Path.Combine(workDir, "sensitiveVariables.json");

        await File.WriteAllBytesAsync(packagePath, payload.PackageBytes, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(variablePath, payload.VariableBytes, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(sensitivePath, payload.SensitiveBytes, ct).ConfigureAwait(false);

        var scriptBody = payload.FillTemplate(packagePath, variablePath, sensitivePath);

        Log.Information("Executing packaged YAML deployment locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync(
            "pwsh", $"-NoProfile -NonInteractive -Command \"{EscapeForCommandLine(scriptBody)}\"",
            workDir, ct).ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteScriptLocallyAsync(
        ScriptExecutionRequest request, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(request.Files, workDir);

        var scriptPath = Path.Combine(workDir, "script.sh");
        await File.WriteAllTextAsync(scriptPath, request.ScriptBody, Encoding.UTF8, ct).ConfigureAwait(false);

        Log.Information("Executing script locally in {WorkDir}", workDir);

        return await _processRunner.RunAsync("bash", scriptPath, workDir, ct).ConfigureAwait(false);
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
}
