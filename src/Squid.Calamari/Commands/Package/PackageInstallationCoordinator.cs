using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

internal static class PackageInstallOptionProperties
{
    public const string SkipIfAlreadyInstalled = "Squid.Action.Package.SkipIfAlreadyInstalled";
    public const string PurgeBeforeInstall = "Squid.Action.Package.PurgeBeforeInstall";
    public const string PreservePaths = "Squid.Action.Package.PreservePaths";
    public const string RetentionCount = "Squid.Action.Package.RetentionCount";
    public const string UseCurrentPointer = "Squid.Action.Package.UseCurrentPointer";
    public const string RollbackOnFailure = "Squid.Action.Package.RollbackOnFailure";
}

internal static class PackageInstallationCoordinator
{
    internal const string InstalledMarkerFileName = ".squid-installed.json";
    internal const string CurrentPointerName = "current";

    public static async Task<PackageInstallResult> InstallAsync(PackageInstallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
            throw new InvalidOperationException($"[hash verification] Package archive not found: '{request.ArchivePath}'.");

        if (string.IsNullOrWhiteSpace(request.FinalInstallationDirectory))
            throw new InvalidOperationException("[target path validation] Final installation directory is required.");

        VerifySha256(request.ArchivePath, request.ExpectedSha256);

        var variables = request.Variables ?? new VariableSet();
        var finalDir = Path.GetFullPath(request.FinalInstallationDirectory);
        var parent = Directory.GetParent(finalDir)?.FullName
            ?? throw new InvalidOperationException($"[target path validation] Cannot resolve parent for '{finalDir}'.");
        var isVersioned = string.Equals(request.Mode, "Versioned", StringComparison.OrdinalIgnoreCase);
        var skipIfInstalled = variables.GetFlag(PackageInstallOptionProperties.SkipIfAlreadyInstalled);
        var purgeBeforeInstall = variables.GetFlag(PackageInstallOptionProperties.PurgeBeforeInstall);
        var useCurrentPointer = isVersioned && variables.GetFlag(PackageInstallOptionProperties.UseCurrentPointer);
        var rollbackOnFailure = variables.GetFlag(PackageInstallOptionProperties.RollbackOnFailure);
        var retentionCount = variables.GetInt32(PackageInstallOptionProperties.RetentionCount) ?? 0;
        var preserveGlobs = SplitMultiLine(variables.Get(PackageInstallOptionProperties.PreservePaths));

        if (skipIfInstalled && IsSameVersionInstalled(finalDir, request.PackageId, request.PackageVersion))
        {
            Console.WriteLine(
                $"SkipIfAlreadyInstalled: package '{request.PackageId}' version '{request.PackageVersion}' already installed at '{finalDir}'.");
            EmitOutputVariables(request, finalDir);
            return new PackageInstallResult(finalDir, FilesExtracted: 0, TotalBytesWritten: 0);
        }

        Directory.CreateDirectory(parent);

        var stagingDir = Path.Combine(parent, $".squid-staging-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(parent, $".squid-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        var filesExtracted = 0;
        long totalBytes = 0;
        var committed = false;
        var finalExistedBefore = Directory.Exists(finalDir);
        string? previousCurrentTarget = null;
        var currentUpdated = false;

        if (useCurrentPointer)
            previousCurrentTarget = TryReadCurrentPointer(parent);

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

            var packageRelativeFiles = GetArchiveRelativeFilePaths(request.ArchivePath, stagingDir);

            await RunConfigRewritePipelineAsync(stagingDir, variables, ct).ConfigureAwait(false);

            CommitDirectory(finalDir, stagingDir, backupDir);
            committed = true;

            if (purgeBeforeInstall)
                PurgeNonPackageFiles(finalDir, packageRelativeFiles, preserveGlobs);

            if (useCurrentPointer)
            {
                UpdateCurrentPointer(parent, finalDir);
                currentUpdated = true;
            }

            if (isVersioned && retentionCount > 0)
                ApplyRetention(parent, retentionCount, finalDir);

            await RunConventionsAsync(request, finalDir, ct).ConfigureAwait(false);

            WriteInstalledMarker(finalDir, request.PackageId, request.PackageVersion);
            EmitOutputVariables(request, finalDir);
            return new PackageInstallResult(finalDir, filesExtracted, totalBytes);
        }
        catch
        {
            if (rollbackOnFailure)
            {
                TryRollbackCommittedInstall(finalDir, backupDir, committed, finalExistedBefore);
                if (useCurrentPointer && currentUpdated)
                    RestoreCurrentPointer(parent, previousCurrentTarget);
            }
            else if (!committed && Directory.Exists(backupDir) && !Directory.Exists(finalDir))
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
            // Backup may have been restored (moved) during rollback; best-effort cleanup.
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

    internal static bool IsSameVersionInstalled(string finalDir, string packageId, string packageVersion)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(packageVersion))
            return false;
        if (!Directory.Exists(finalDir))
            return false;

        var markerPath = Path.Combine(finalDir, InstalledMarkerFileName);
        if (!File.Exists(markerPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = doc.RootElement;
            var installedId = root.TryGetProperty("packageId", out var idEl) ? idEl.GetString() : null;
            var installedVersion = root.TryGetProperty("version", out var verEl) ? verEl.GetString() : null;
            return string.Equals(installedId, packageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(installedVersion, packageVersion, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[skip-if-installed] Failed to read marker '{markerPath}': {ex.Message}");
            return false;
        }
    }

    internal static void WriteInstalledMarker(string finalDir, string packageId, string packageVersion)
    {
        Directory.CreateDirectory(finalDir);
        var payload = new
        {
            packageId = packageId ?? string.Empty,
            version = packageVersion ?? string.Empty,
            installedAtUtc = DateTime.UtcNow.ToString("O")
        };
        var json = JsonSerializer.Serialize(payload);
        File.WriteAllText(Path.Combine(finalDir, InstalledMarkerFileName), json, Encoding.UTF8);
    }

    private static async Task RunConfigRewritePipelineAsync(string stagingDir, VariableSet variables, CancellationToken ct)
    {
        // Minimal adapter: existing rewriter steps are typed against RunScriptCommandContext.
        // For package installs we only need WorkingDirectory + Variables; ScriptPath/VariablesPath
        // are required members but unused by these steps.
        var context = new RunScriptCommandContext
        {
            ScriptPath = string.Empty,
            VariablesPath = string.Empty,
            WorkingDirectory = stagingDir,
            Variables = variables
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

    private static void TryRollbackCommittedInstall(string finalDir, string backupDir, bool committed, bool finalExistedBefore)
    {
        if (!committed)
        {
            if (Directory.Exists(backupDir) && !Directory.Exists(finalDir))
            {
                try { Directory.Move(backupDir, finalDir); }
                catch (Exception restoreEx)
                {
                    Console.Error.WriteLine($"[rollback] Failed to restore backup after error: {restoreEx.Message}");
                }
            }
            return;
        }

        try
        {
            if (Directory.Exists(finalDir))
                TryDeleteDirectory(finalDir);

            if (finalExistedBefore && Directory.Exists(backupDir))
                Directory.Move(backupDir, finalDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[rollback] Failed to restore previous installation. Backup path: '{backupDir}'. Error: {ex.Message}");
        }
    }

    private static void PurgeNonPackageFiles(
        string finalDir,
        IReadOnlySet<string> packageRelativeFiles,
        IReadOnlyList<string> preserveGlobs)
    {
        foreach (var file in Directory.GetFiles(finalDir, "*", SearchOption.AllDirectories))
        {
            var rel = NormalizeRelativePath(finalDir, file);
            if (string.Equals(rel, InstalledMarkerFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (packageRelativeFiles.Contains(rel) || IsPreservedByGlob(rel, preserveGlobs))
                continue;

            TryDeleteFile(file);
        }

        // Remove empty directories left behind (deepest first), never the root.
        foreach (var dir in Directory.GetDirectories(finalDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static bool IsPreservedByGlob(string relativePath, IReadOnlyList<string> preserveGlobs)
    {
        var normalised = relativePath.Replace('\\', '/');
        foreach (var glob in preserveGlobs)
        {
            if (string.IsNullOrWhiteSpace(glob))
                continue;

            // Prefer existing GlobMatcher expand semantics when possible by testing
            // the pattern regex directly against the relative path.
            var regex = GlobMatcher.GlobToRegex(glob.Replace('\\', '/'));
            if (regex.IsMatch(normalised))
                return true;
        }
        return false;
    }

    private static IReadOnlySet<string> GetArchiveRelativeFilePaths(string archivePath, string stagingDir)
    {
        var fromArchive = TryListZipRelativePaths(archivePath);
        if (fromArchive is not null)
            return fromArchive;

        // Fallback for non-zip formats: treat every extracted staging file as package content.
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(stagingDir))
        {
            foreach (var file in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
                set.Add(NormalizeRelativePath(stagingDir, file));
        }
        return set;
    }

    private static HashSet<string>? TryListZipRelativePaths(string archivePath)
    {
        var lower = archivePath.ToLowerInvariant();
        if (!(lower.EndsWith(".zip", StringComparison.Ordinal) || lower.EndsWith(".nupkg", StringComparison.Ordinal)))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                var rel = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (rel.EndsWith('/'))
                    continue;
                set.Add(rel);
            }
            return set;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyRetention(string packageRoot, int keepCount, string currentFinalDir)
    {
        try
        {
            if (!Directory.Exists(packageRoot) || keepCount <= 0)
                return;

            var currentFull = Path.GetFullPath(currentFinalDir);
            var versionDirs = Directory.GetDirectories(packageRoot)
                .Where(dir =>
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, CurrentPointerName, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (name.StartsWith(".squid-", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                })
                .Select(dir => new DirectoryInfo(dir))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ThenByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentFull };
            foreach (var dir in versionDirs)
            {
                if (keep.Count >= keepCount)
                    break;
                keep.Add(dir.FullName);
            }

            foreach (var dir in versionDirs)
            {
                if (keep.Contains(dir.FullName))
                    continue;
                TryDeleteDirectory(dir.FullName);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[retention] Failed to apply retention under '{packageRoot}': {ex.Message}");
        }
    }

    internal static void UpdateCurrentPointer(string packageRoot, string finalDir)
    {
        Directory.CreateDirectory(packageRoot);
        var currentPath = Path.Combine(packageRoot, CurrentPointerName);
        var targetFull = Path.GetFullPath(finalDir);
        var targetRelative = Path.GetFileName(targetFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        RemoveCurrentPointer(currentPath);

        try
        {
            // Prefer relative symlink for portability across machines sharing the package root.
            Directory.CreateSymbolicLink(currentPath, targetRelative);
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[current-pointer] Symlink unavailable ({ex.GetType().Name}); writing pointer file.");
        }

        File.WriteAllText(currentPath, targetRelative + Environment.NewLine, Encoding.UTF8);
    }

    private static void RestoreCurrentPointer(string packageRoot, string? previousTarget)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previousTarget))
            {
                RemoveCurrentPointer(Path.Combine(packageRoot, CurrentPointerName));
                return;
            }

            UpdateCurrentPointer(packageRoot, previousTarget);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rollback] Failed to restore current pointer under '{packageRoot}': {ex.Message}");
        }
    }

    internal static string? TryReadCurrentPointer(string packageRoot)
    {
        var currentPath = Path.Combine(packageRoot, CurrentPointerName);
        if (!Directory.Exists(currentPath) && !File.Exists(currentPath))
            return null;

        try
        {
            var linkInfo = new FileInfo(currentPath);
            if (!string.IsNullOrEmpty(linkInfo.LinkTarget))
            {
                var target = linkInfo.LinkTarget;
                return Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(packageRoot, target));
            }

            if (Directory.Exists(currentPath))
            {
                var resolved = Directory.ResolveLinkTarget(currentPath, returnFinalTarget: true);
                if (resolved is not null)
                    return Path.GetFullPath(resolved.FullName);
            }

            if (File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                var pointer = File.ReadAllText(currentPath).Trim();
                if (string.IsNullOrWhiteSpace(pointer))
                    return null;
                return Path.IsPathRooted(pointer)
                    ? Path.GetFullPath(pointer)
                    : Path.GetFullPath(Path.Combine(packageRoot, pointer));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[current-pointer] Failed to read '{currentPath}': {ex.Message}");
        }

        return null;
    }

    private static void RemoveCurrentPointer(string currentPath)
    {
        try
        {
            if (Directory.Exists(currentPath))
            {
                // Symlink directories must be deleted without recursing into the target.
                Directory.Delete(currentPath, recursive: false);
                return;
            }

            if (File.Exists(currentPath))
                File.Delete(currentPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[current-pointer] Failed to remove '{currentPath}': {ex.Message}");
            TryDeleteDirectory(currentPath);
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

    private static IReadOnlyList<string> SplitMultiLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string NormalizeRelativePath(string rootDir, string absolutePath)
    {
        var rel = Path.GetRelativePath(rootDir, absolutePath);
        return rel.Replace('\\', '/');
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
