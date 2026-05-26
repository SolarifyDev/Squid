using Squid.Calamari.Pipeline;

namespace Squid.Calamari.Commands.Package;

/// <summary>
/// Wire-contract constants for the package-extract feature (G1.4).
/// Public so cross-project drift tests can pin them without
/// InternalsVisibleTo pollution.
///
/// <para><b>Canonical-only — no Legacy nested class</b>: this is a new
/// surface. No existing operator deployment emits the variable yet (the
/// IIS handler PS1 does its own extraction in-script before any Calamari
/// step runs). Future handlers + future IIS handler refactors will emit
/// <see cref="OriginalPath"/> when they want Calamari to do the extract.</para>
/// </summary>
public static class PackageVariableNames
{
    /// <summary>Path on disk to the package file (typically a <c>.nupkg</c>
    /// or <c>.zip</c> downloaded by the server and dropped on the agent).
    /// Step is a no-op if absent / blank.</summary>
    public const string OriginalPath = "Squid.Action.Package.OriginalPath";
}

/// <summary>
/// G1.4 — extracts a package archive (<c>.nupkg</c> / <c>.zip</c>) into
/// <c>context.WorkingDirectory</c>. Runs BEFORE the rewriter steps so
/// substitution / XDT / JSON replacement operate on the extracted files.
///
/// <para><b>Pipeline position</b>: <c>LoadVariablesFromFiles → ExtractPackage
/// → SubstituteInFiles → ConfigurationTransforms → JsonConfigVariables →
/// (operator script)</c>. ExtractPackage MUST run after variables are loaded
/// (so the wire-literal lookup works) and before any rewriter (so they
/// have files to rewrite).</para>
///
/// <para><b>No-op shape</b>: if <see cref="PackageVariableNames.OriginalPath"/>
/// is not set in the variable set, the step skips silently. Operators
/// running a standalone bash script (no package, just inline content) hit
/// this path — same operator UX as before G1.4.</para>
///
/// <para><b>Safety</b>: <see cref="ZipExtractor"/> handles zip-slip,
/// absolute paths, per-entry + total size caps. Failure modes are logged
/// + the step throws — extraction is foundational; partial extracts would
/// leave rewriter steps operating on a half-set of files which is worse
/// than failing fast.</para>
/// </summary>
internal sealed class ExtractPackageStep : ExecutionStep<RunScriptCommandContext>
{
    /// <summary>Accepted archive extensions. Lower-cased for comparison.
    /// nupkg is just a zip with a different filename suffix — same engine
    /// handles both.</summary>
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".nupkg", ".zip" };

    public override bool IsEnabled(RunScriptCommandContext context)
    {
        if (context.Variables is null) return false;
        // Gate on the wire literal: no path → no extract. Cheap pre-check
        // so the step doesn't burden every standalone-script deploy.
        var path = context.Variables.Get(PackageVariableNames.OriginalPath);
        return !string.IsNullOrWhiteSpace(path);
    }

    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                "Working directory has not been initialized — ExtractPackageStep must run after ResolveWorkingDirectoryStep.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                "Variables have not been loaded — ExtractPackageStep must run after LoadVariablesFromFilesStep.");

        var archivePath = context.Variables.Get(PackageVariableNames.OriginalPath)!.Trim();

        var ext = Path.GetExtension(archivePath);
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"ExtractPackageStep: package '{archivePath}' has unsupported extension '{ext}'. " +
                $"Supported: {string.Join(", ", AllowedExtensions)}. Skipping the extract step requires unsetting {PackageVariableNames.OriginalPath}.");

        var result = ZipExtractor.Extract(archivePath, context.WorkingDirectory);

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"ExtractPackageStep: failed to extract '{archivePath}' into '{context.WorkingDirectory}': {result.FailureReason}");

        Console.WriteLine(
            $"ExtractPackage: '{archivePath}' → '{context.WorkingDirectory}' " +
            $"({result.FilesExtracted:N0} file(s), {result.TotalBytesWritten:N0} bytes).");

        return Task.CompletedTask;
    }
}
