using Squid.Calamari.Commands.Common;
using Squid.Calamari.Pipeline;

namespace Squid.Calamari.Commands.Substitution;

/// <summary>
/// G1.1 — pipeline step that does <c>#{Token}</c> substitution on operator-
/// nominated files (per IIS handler's <c>SubstituteInFiles.TargetFiles</c> globs).
///
/// <para><b>Why this exists</b>: pre-G1.1 the IIS handler advertised a
/// "Substitute variables in files" toggle but no Calamari step actually
/// consumed the variables. Operators saw the UI toggle, set
/// <c>SubstituteInFiles.Enabled = True</c>, and the deployment ran without
/// any substitution happening — silently. The parent decision document
/// (Squid Calamari Architecture Decision 2026-05-23) committed to filling
/// out these advertised features inside Calamari (rather than moving them
/// server-side) precisely because XDT-style + token-replacement work needs
/// to happen on the agent where the extracted files live.</para>
///
/// <para><b>Operator semantics</b> (mirrors Octopus's
/// <c>SubstituteInFilesBehaviour</c>):
/// <list type="bullet">
///   <item>Gated by <see cref="EnabledVariableName"/> (default OFF — skip
///         entirely if anything other than <c>"True"</c>).</item>
///   <item>Files specified by <see cref="TargetFilesVariableName"/> as
///         newline-separated globs (relative to working dir).</item>
///   <item>Tokens matching <c>#{Name.Of.Variable}</c> are replaced with the
///         variable's value from the loaded <c>VariableSet</c>.</item>
///   <item>Unknown tokens are LENIENT by default — left as-is. Operator can
///         flip <see cref="ShouldFailOnUnresolvedVariableName"/> to "True"
///         to make any unresolved token fail the whole deployment.</item>
///   <item>UTF-8 BOM is preserved on round-trip; encoding inferred from the
///         first 3 bytes of the file.</item>
/// </list></para>
///
/// <para><b>Failure modes the step deliberately ignores</b> (logs + continues):
/// <list type="bullet">
///   <item>Glob matches no files — operator typo or extract location
///         mismatch; not worth failing the whole deploy.</item>
///   <item>Binary file matches glob — skip silently (binary detection by
///         leading null byte).</item>
///   <item>Single file unreadable (permissions, locked) — log + skip that
///         file, continue with the rest.</item>
/// </list>
/// All of these surface as Serilog warnings the operator can see in the
/// deploy log + activity timeline.</para>
/// </summary>
/// <summary>
/// Wire-contract constants for the SubstituteInFiles feature. Hosted in a
/// PUBLIC class so the cross-project drift test in Squid.UnitTests can
/// reference them without InternalsVisibleTo pollution. The step
/// implementation stays internal — only the literals are public.
///
/// <para><b>Canonical vs Legacy</b>: the top-level constants
/// (<see cref="Enabled"/>, <see cref="TargetFiles"/>) are the
/// <b>canonical</b>, handler-agnostic wire literals — preferred for new
/// handlers (Docker, nginx, generic RunScript, …) and what
/// <see cref="SubstituteInFilesStep"/> reads first. The nested
/// <see cref="Legacy"/> class holds the IIS-handler-specific names that
/// existing operator deployments still emit via
/// <c>IISDeployProperties</c>. The step falls back to those when the
/// canonical name is absent — fully back-compat with deploys saved before
/// the canonical surface existed.</para>
/// </summary>
public static class SubstituteInFilesVariableNames
{
    /// <summary>Canonical, handler-agnostic Enabled toggle. Preferred for
    /// new handlers + Squid.Web migrations.</summary>
    public const string Enabled = "Squid.Action.SubstituteInFiles.Enabled";

    /// <summary>Canonical, handler-agnostic TargetFiles glob list.</summary>
    public const string TargetFiles = "Squid.Action.SubstituteInFiles.TargetFiles";

    /// <summary>Strict-mode toggle — already handler-agnostic in the wire
    /// (never had an IIS-prefixed variant). Pinned by test (Rule 8).</summary>
    public const string ShouldFailOnUnresolved =
        "Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails";

    /// <summary>
    /// Legacy IIS-handler-specific wire literals. The IIS handler's PS1
    /// script + existing operator deployment definitions emit these.
    /// <see cref="SubstituteInFilesStep"/> falls back to these names when
    /// the canonical literals above are not set. Do not use for new
    /// handlers — emit the canonical names instead.
    /// </summary>
    public static class Legacy
    {
        /// <summary>IIS-specific Enabled — kept for back-compat.</summary>
        public const string Enabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";

        /// <summary>IIS-specific TargetFiles — kept for back-compat.</summary>
        public const string TargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";
    }
}

internal sealed class SubstituteInFilesStep : ExecutionStep<RunScriptCommandContext>
{
    /// <summary>Re-exported for backward-compat with the original test
    /// suite that referenced these on the step. The values point at the
    /// CANONICAL names — tests writing to these will exercise the
    /// canonical-first read path. To exercise the legacy fallback
    /// explicitly, write to <c>SubstituteInFilesVariableNames.Legacy.Enabled</c>.</summary>
    internal const string EnabledVariableName = SubstituteInFilesVariableNames.Enabled;
    internal const string TargetFilesVariableName = SubstituteInFilesVariableNames.TargetFiles;
    internal const string ShouldFailOnUnresolvedVariableName = SubstituteInFilesVariableNames.ShouldFailOnUnresolved;

    public override bool IsEnabled(RunScriptCommandContext context)
    {
        if (context.Variables is null) return false;

        // Canonical first, legacy fallback. Either-name-set wins → True.
        // Pinned by IsEnabled_LegacyIISName_StillRunsStep + IsEnabled_CanonicalName_RunsStep.
        var raw = context.Variables.Get(SubstituteInFilesVariableNames.Enabled)
                  ?? context.Variables.Get(SubstituteInFilesVariableNames.Legacy.Enabled);
        return string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException("Working directory has not been initialized — SubstituteInFilesStep must run after ResolveWorkingDirectoryStep.");
        if (context.Variables is null)
            throw new InvalidOperationException("Variables have not been loaded — SubstituteInFilesStep must run after LoadVariablesFromFilesStep.");

        // Canonical first, legacy fallback for both Enabled-gate and TargetFiles
        // glob list. Operators with deploys saved before the canonical surface
        // existed still hit the legacy path; new handlers emit the canonical name.
        var targetFilesRaw = context.Variables.Get(SubstituteInFilesVariableNames.TargetFiles)
                             ?? context.Variables.Get(SubstituteInFilesVariableNames.Legacy.TargetFiles);
        if (string.IsNullOrWhiteSpace(targetFilesRaw)) return;    // no-op, operator left blank

        // ShouldFailOnUnresolved was always handler-agnostic in the wire — no legacy variant.
        var failOnUnresolved = string.Equals(
            context.Variables.Get(SubstituteInFilesVariableNames.ShouldFailOnUnresolved),
            "True",
            StringComparison.OrdinalIgnoreCase);

        var globs = SplitGlobs(targetFilesRaw);
        var allUnresolved = new List<(string file, IReadOnlyList<string> tokens)>();
        var filesProcessed = 0;
        var filesSkipped = 0;

        foreach (var glob in globs)
        {
            ct.ThrowIfCancellationRequested();

            var matches = GlobMatcher.Expand(context.WorkingDirectory, glob).ToList();

            if (matches.Count == 0)
            {
                // Calamari output convention: stderr for warnings. The Tentacle
                // host captures stdout + stderr line-by-line and forwards both
                // into the deploy log; stderr lines surface in the activity
                // timeline tagged as warnings.
                Console.Error.WriteLine(
                    $"::warning::SubstituteInFiles: glob '{glob}' matched no files under '{context.WorkingDirectory}' " +
                    "— operator typo, or files extracted to a different location? Continuing.");
                continue;
            }

            foreach (var file in matches)
            {
                try
                {
                    if (IsBinaryFile(file))
                    {
                        Console.WriteLine($"SubstituteInFiles: skipping '{file}' (binary content detected).");
                        filesSkipped++;
                        continue;
                    }

                    var (text, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(file);
                    var result = TokenSubstituter.Replace(text, context.Variables);

                    if (result.UnresolvedTokens.Count > 0)
                        allUnresolved.Add((file, result.UnresolvedTokens));

                    EncodingPreservingFileIO.WriteAllTextAtomic(file, result.Output, encoding);
                    filesProcessed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"::warning::SubstituteInFiles: failed to process '{file}' — skipping. {ex.GetType().Name}: {ex.Message}");
                    filesSkipped++;
                }
            }
        }

        Console.WriteLine(
            $"SubstituteInFiles: processed {filesProcessed} file(s), skipped {filesSkipped}. " +
            $"Unresolved tokens in {allUnresolved.Count} file(s).");

        if (failOnUnresolved && allUnresolved.Count > 0)
            throw new SubstituteInFilesException(allUnresolved);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Operator wire format: newline-separated. Tolerate <c>\r\n</c> +
    /// <c>\n</c> + whitespace-only lines (operators paste from various
    /// editors). Each line is one glob.
    /// </summary>
    private static IEnumerable<string> SplitGlobs(string raw)
        => raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

    /// <summary>
    /// Detect binary files by scanning the first 8KB for a null byte. Same
    /// heuristic git uses. Fast — single read + linear scan.
    /// </summary>
    private static bool IsBinaryFile(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[Math.Min(8192, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);

            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return true;

            return false;
        }
        catch
        {
            // If we can't read it to detect, treat as binary → skip safely.
            return true;
        }
    }

}

/// <summary>
/// Strict-mode failure for SubstituteInFiles — raised only when the operator
/// has set <c>Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails = True</c>
/// AND at least one token in the target files was unresolved. Message lists
/// the unresolved tokens grouped by file so the operator can fix their
/// variable set without grep-and-guess.
/// </summary>
internal sealed class SubstituteInFilesException : Exception
{
    public SubstituteInFilesException(IReadOnlyList<(string file, IReadOnlyList<string> tokens)> unresolvedPerFile)
        : base(BuildMessage(unresolvedPerFile))
    {
        UnresolvedPerFile = unresolvedPerFile;
    }

    public IReadOnlyList<(string file, IReadOnlyList<string> tokens)> UnresolvedPerFile { get; }

    private static string BuildMessage(IReadOnlyList<(string file, IReadOnlyList<string> tokens)> unresolvedPerFile)
    {
        var lines = new List<string>
        {
            "SubstituteInFiles: deployment failed because one or more #{Token} references could not be resolved " +
            "AND Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails is set to True."
        };

        foreach (var (file, tokens) in unresolvedPerFile)
            lines.Add($"  {file}: {string.Join(", ", tokens.Distinct())}");

        lines.Add(
            "Either: (a) add the missing variables to your project / environment / library variable set, " +
            "(b) flip ShouldFailDeploymentOnSubstitutionFails to False to allow lenient (Octostache-compatible) behavior, " +
            "or (c) remove the unresolved tokens from your target files.");

        return string.Join('\n', lines);
    }
}
