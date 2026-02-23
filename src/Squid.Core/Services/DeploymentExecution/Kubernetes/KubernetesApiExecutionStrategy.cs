using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.Common;
using Squid.Core.Settings.GithubPackage;
using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public partial class KubernetesApiExecutionStrategy : IExecutionStrategy
{
    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly CalamariGithubPackageSetting _calamariGithubPackageSetting;

    public KubernetesApiExecutionStrategy(
        IYamlNuGetPacker yamlNuGetPacker,
        CalamariGithubPackageSetting calamariGithubPackageSetting)
    {
        _yamlNuGetPacker = yamlNuGetPacker;
        _calamariGithubPackageSetting = calamariGithubPackageSetting;
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

        var yamlNuGetPackageBytes = CreateYamlNuGetPackage(request.Files);
        var (variableJson, sensitiveVariableJson, sensitivePassword) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var packageFileName = $"squid.{request.ReleaseVersion}.nupkg";
        var packagePath = Path.Combine(workDir, packageFileName);
        await File.WriteAllBytesAsync(packagePath, yamlNuGetPackageBytes, ct).ConfigureAwait(false);

        var variablePath = Path.Combine(workDir, "variables.json");
        await File.WriteAllBytesAsync(variablePath, variableJson, ct).ConfigureAwait(false);

        var sensitivePath = Path.Combine(workDir, "sensitiveVariables.json");
        await File.WriteAllBytesAsync(sensitivePath, sensitiveVariableJson, ct).ConfigureAwait(false);

        if (request.CalamariPackageBytes is { Length: > 0 })
        {
            var calamariPath = Path.Combine(workDir, "Calamari");
            ExtractCalamariPackage(request.CalamariPackageBytes, calamariPath);
        }

        var deployScript = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1");
        var calamariVersion = GetCalamariVersion();

        var scriptBody = deployScript
            .Replace("{{PackageFilePath}}", packagePath, StringComparison.Ordinal)
            .Replace("{{VariableFilePath}}", variablePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariableFile}}", string.IsNullOrEmpty(sensitivePassword) ? string.Empty : sensitivePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariablePassword}}", sensitivePassword, StringComparison.Ordinal)
            .Replace("{{CalamariVersion}}", calamariVersion, StringComparison.Ordinal);

        Log.Information("Executing Calamari locally in {WorkDir}", workDir);

        return await RunProcessAsync("pwsh", $"-NoProfile -NonInteractive -Command \"{EscapeForCommandLine(scriptBody)}\"", workDir, ct)
            .ConfigureAwait(false);
    }

    private async Task<ScriptExecutionResult> ExecuteScriptLocallyAsync(
        ScriptExecutionRequest request, string workDir, CancellationToken ct)
    {
        WriteFilesToDirectory(request.Files, workDir);

        var scriptPath = Path.Combine(workDir, "script.sh");
        await File.WriteAllTextAsync(scriptPath, request.ScriptBody, Encoding.UTF8, ct).ConfigureAwait(false);

        Log.Information("Executing script locally in {WorkDir}", workDir);

        return await RunProcessAsync("bash", scriptPath, workDir, ct).ConfigureAwait(false);
    }

    private byte[] CreateYamlNuGetPackage(Dictionary<string, byte[]> files)
    {
        if (files == null || files.Count == 0)
            return Array.Empty<byte>();

        var yamlStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
            yamlStreams[file.Key] = new MemoryStream(file.Value);

        return _yamlNuGetPacker.CreateNuGetPackageFromYamlStreams(yamlStreams);
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

    private static void ExtractCalamariPackage(byte[] packageBytes, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.Combine(targetDir, entry.FullName);
            var entryDir = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(entryDir))
                Directory.CreateDirectory(entryDir);

            if (string.IsNullOrEmpty(entry.Name)) continue;

            using var entryStream = entry.Open();
            using var fileStream = File.Create(destinationPath);
            entryStream.CopyTo(fileStream);
        }
    }

    private string GetCalamariVersion()
        => string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.Version)
            ? "28.2.1"
            : _calamariGithubPackageSetting.Version;

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
