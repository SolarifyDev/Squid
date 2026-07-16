using System.Security.Cryptography;
using System.Text;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Package;

internal sealed record PackageInstallRequest
{
    public required string ArchivePath { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required string Mode { get; init; }
    public required string FinalInstallationDirectory { get; init; }
    public ScriptSyntax PreferredSyntax { get; init; } = ScriptSyntax.Bash;
    public VariableSet? Variables { get; init; }
    public IScriptEngine? ScriptEngine { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
}

internal sealed record PackageInstallResult(string InstallationDirectory, int FilesExtracted, long TotalBytesWritten);

internal static class PackageInstallationCoordinator
{
    public static async Task<PackageInstallResult> InstallAsync(PackageInstallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
            throw new InvalidOperationException($"[hash verification] Package archive not found: '{request.ArchivePath}'.");

        if (string.IsNullOrWhiteSpace(request.FinalInstallationDirectory))
            throw new InvalidOperationException("[target path validation] Final installation directory is required.");

        VerifySha256(request.ArchivePath, request.ExpectedSha256);

        var finalDir = Path.GetFullPath(request.FinalInstallationDirectory);
        var parent = Directory.GetParent(finalDir)?.FullName
            ?? throw new InvalidOperationException($"[target path validation] Cannot resolve parent for '{finalDir}'.");

        Directory.CreateDirectory(parent);

        var stagingDir = Path.Combine(parent, $".squid-staging-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(parent, $".squid-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        var filesExtracted = 0;
        long totalBytes = 0;
        var committed = false;

        try
        {
            if (string.Equals(request.Mode, "Custom", StringComparison.OrdinalIgnoreCase) && Directory.Exists(finalDir))
                CopyDirectory(finalDir, stagingDir);

            var extractor = PackageExtractorRegistry.Resolve(request.ArchivePath)
                ?? throw new InvalidOperationException(
                    $"[extraction] Unsupported package archive '{request.ArchivePath}'. Supported: {string.Join(", ", PackageExtractorRegistry.SupportedExtensions)}.");

            var extractResult = extractor.Extract(request.ArchivePath, stagingDir);
            if (!extractResult.Succeeded)
                throw new InvalidOperationException($"[extraction] {extractResult.FailureReason}");

            filesExtracted = extractResult.FilesExtracted;
            totalBytes = extractResult.TotalBytesWritten;

            await RunConfigRewritePipelineAsync(stagingDir, request.Variables, ct).ConfigureAwait(false);

            CommitDirectory(finalDir, stagingDir, backupDir);
            committed = true;

            await RunConventionsAsync(request, finalDir, ct).ConfigureAwait(false);

            EmitOutputVariables(request, finalDir);
            return new PackageInstallResult(finalDir, filesExtracted, totalBytes);
        }
        catch
        {
            if (!committed && Directory.Exists(backupDir) && !Directory.Exists(finalDir))
            {
                try { Directory.Move(backupDir, finalDir); }
                catch (Exception restoreEx)
                {
                    Console.Error.WriteLine($"[final-directory commit] Failed to restore backup after error: {restoreEx.Message}");
                }
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
            if (committed)
                TryDeleteDirectory(backupDir);
        }
    }

    internal static void VerifySha256(string archivePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidOperationException("[hash verification] Expected SHA-256 is required.");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException($"[hash verification] SHA-256 mismatch for '{archivePath}': expected {expectedSha256}, got {actual}.");
    }

    private static async Task RunConfigRewritePipelineAsync(string stagingDir, VariableSet? variables, CancellationToken ct)
    {
        // Minimal adapter: existing rewriter steps are typed against RunScriptCommandContext.
        // For package installs we only need WorkingDirectory + Variables; ScriptPath/VariablesPath
        // are required members but unused by these steps.
        var context = new RunScriptCommandContext
        {
            ScriptPath = string.Empty,
            VariablesPath = string.Empty,
            WorkingDirectory = stagingDir,
            Variables = variables ?? new VariableSet()
        };

        ExecutionStep<RunScriptCommandContext>[] steps =
        [
            new SubstituteInFilesStep(),
            new ConfigurationTransformsStep(),
            new ConfigurationVariablesStep(),
            new StructuredConfigVariablesStep()
        ];

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!step.IsEnabled(context))
                continue;

            await step.ExecuteAsync(context, ct).ConfigureAwait(false);
        }
    }

    private static void CommitDirectory(string finalDir, string stagingDir, string backupDir)
    {
        try
        {
            if (Directory.Exists(finalDir))
                Directory.Move(finalDir, backupDir);

            Directory.Move(stagingDir, finalDir);
        }
        catch (Exception ex)
        {
            if (Directory.Exists(backupDir) && !Directory.Exists(finalDir))
            {
                try { Directory.Move(backupDir, finalDir); }
                catch (Exception restoreEx)
                {
                    throw new InvalidOperationException(
                        $"[final-directory commit] Commit failed: {ex.Message}. Backup restore also failed: {restoreEx.Message}", ex);
                }
            }

            throw new InvalidOperationException($"[final-directory commit] {ex.Message}", ex);
        }
    }

    private static async Task RunConventionsAsync(PackageInstallRequest request, string finalDir, CancellationToken ct)
    {
        var engine = request.ScriptEngine ?? new ScriptEngine();
        var variables = request.Variables ?? new VariableSet();

        await RunOneConventionAsync(engine, variables, finalDir, ConventionScriptNames.PreDeploy, request.PreferredSyntax, ct)
            .ConfigureAwait(false);
        // Empty main action by design.
        await RunOneConventionAsync(engine, variables, finalDir, ConventionScriptNames.PostDeploy, request.PreferredSyntax, ct)
            .ConfigureAwait(false);
    }

    private static async Task RunOneConventionAsync(
        IScriptEngine engine,
        VariableSet variables,
        string finalDir,
        string conventionName,
        ScriptSyntax preferredSyntax,
        CancellationToken ct)
    {
        var resolved = ConventionScriptResolver.Resolve(finalDir, conventionName, preferredSyntax);
        if (resolved is null)
            return;

        Console.WriteLine($"{conventionName}: running '{resolved.Value.Path}' ({resolved.Value.Syntax}).");

        // Lightweight bootstrap for durable-install path: export variables then run script body.
        var original = File.ReadAllText(resolved.Value.Path);
        var preamble = resolved.Value.Syntax == ScriptSyntax.PowerShell
            ? PowerShellVariableBootstrapper.GeneratePreamble(variables)
            : VariableBootstrapper.GeneratePreamble(variables);
        var extension = resolved.Value.Syntax == ScriptSyntax.PowerShell ? ".ps1" : ".sh";
        var bootstrapped = Path.Combine(finalDir, $".squid-{conventionName.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(bootstrapped, preamble + original);

        try
        {
            var result = await engine.ExecuteAsync(new ScriptExecutionRequest
            {
                ScriptPath = bootstrapped,
                WorkingDirectory = finalDir,
                Syntax = resolved.Value.Syntax,
                OutputProcessor = new Squid.Calamari.Execution.ScriptOutputProcessor()
            }, ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"[{conventionName}] hook script '{resolved.Value.Path}' exited with code {result.ExitCode}.");
        }
        finally
        {
            TryDeleteFile(bootstrapped);
        }
    }

    private static void EmitOutputVariables(PackageInstallRequest request, string finalDir)
    {
        WriteServiceMessage("Squid.Action.Package.InstallationDirectoryPath", finalDir);
        if (!string.IsNullOrWhiteSpace(request.PackageId))
            WriteServiceMessage("Squid.Action.Package.PackageId", request.PackageId);
        if (!string.IsNullOrWhiteSpace(request.PackageVersion))
            WriteServiceMessage("Squid.Action.Package.PackageVersion", request.PackageVersion);
    }

    private static void WriteServiceMessage(string name, string value)
    {
        var escapedName = name.Replace("'", "''", StringComparison.Ordinal);
        var escapedValue = value.Replace("'", "''", StringComparison.Ordinal);
        Console.WriteLine($"##squid[setVariable name='{escapedName}' value='{escapedValue}']");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir));
            Directory.CreateDirectory(target);
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cleanup] Failed to delete '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
