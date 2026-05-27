using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Pipeline;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// Wire-contract constants for the JSON-leaf configuration-variable feature.
/// Public so cross-project drift tests in Squid.UnitTests can pin them.
///
/// <para><b>Canonical naming</b>: the canonical wire literals use
/// <c>Squid.Action.JsonConfigVariables.*</c> — matches the frontend feature
/// ID (<c>Squid.Features.JsonConfigurationVariables</c>) and accurately
/// describes what the step does (JSON-only leaf replacement). The legacy
/// literal <c>StructuredConfigurationVariables</c> is kept for back-compat
/// with the IIS handler's existing emission + operator deployments saved
/// before the rename.</para>
///
/// <para>The C# class keeps the original name <c>StructuredConfigVariableNames</c>
/// to avoid rippling-rename through tests; <see cref="JsonConfigVariableNames"/>
/// is provided as a forward-looking alias for new handlers.</para>
/// </summary>
public static class StructuredConfigVariableNames
{
    /// <summary>Canonical, handler-agnostic Enabled toggle.</summary>
    public const string Enabled = "Squid.Action.JsonConfigVariables.Enabled";

    /// <summary>Canonical, handler-agnostic Targets glob list.</summary>
    public const string Targets = "Squid.Action.JsonConfigVariables.Targets";

    /// <summary>
    /// Legacy IIS-handler-specific wire literals. Existing operator deployments
    /// emit these. <see cref="StructuredConfigVariablesStep"/> falls back to
    /// these names when the canonical literals above are not set.
    /// </summary>
    public static class Legacy
    {
        public const string Enabled = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled";
        public const string Targets = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets";
    }
}

/// <summary>Forward-looking alias matching the canonical feature name
/// (<c>JsonConfigVariables</c>). New handlers SHOULD reference this name —
/// it telegraphs that the toggle is JSON-only, not "structured config"
/// in the generic sense (no YAML / XML / Properties support).</summary>
public static class JsonConfigVariableNames
{
    public const string Enabled = StructuredConfigVariableNames.Enabled;
    public const string Targets = StructuredConfigVariableNames.Targets;
}

/// <summary>
/// G1.3 — applies <see cref="JsonPathReplacer"/> to operator-nominated JSON
/// files. Closes the IIS handler's third advertised cross-cutting feature
/// (after G1.1 SubstituteInFiles + G1.2 ConfigurationTransforms).
///
/// <para><b>Pipeline position</b>: must run AFTER SubstituteInFilesStep so
/// any <c>#{Token}</c> references inside the JSON file are resolved first.
/// Then JSON-path replacement operates on concrete leaf values.</para>
///
/// <para><b>Scope</b>: targets are operator-supplied newline-separated globs.
/// Typically <c>appsettings.json</c> + <c>appsettings.{Env}.json</c>, but
/// works on any JSON file matching the glob.</para>
///
/// <para><b>Glob reuse</b>: <see cref="GlobMatcher"/> is shared with
/// SubstituteInFilesStep — single source of truth for glob semantics,
/// path-traversal sandbox, and recursive matching.</para>
/// </summary>
internal sealed class StructuredConfigVariablesStep : ExecutionStep<RunScriptCommandContext>
{
    public const string StepName = "StructuredConfigVariables";

    public override bool IsEnabled(RunScriptCommandContext context)
    {
        if (context.Variables is null) return false;
        // Canonical first, legacy fallback.
        var raw = context.Variables.Get(StructuredConfigVariableNames.Enabled)
                  ?? context.Variables.Get(StructuredConfigVariableNames.Legacy.Enabled);
        return string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase);
    }

    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                "Working directory has not been initialized — StructuredConfigVariablesStep must run after ResolveWorkingDirectoryStep.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                "Variables have not been loaded — StructuredConfigVariablesStep must run after LoadVariablesFromFilesStep.");

        // Canonical first, legacy fallback for the Targets glob list.
        var targetsRaw = context.Variables.Get(StructuredConfigVariableNames.Targets)
                         ?? context.Variables.Get(StructuredConfigVariableNames.Legacy.Targets);
        if (string.IsNullOrWhiteSpace(targetsRaw))
        {
            context.StepOutcomes.Add(StepOutcome.Skipped(StepName, "Targets glob is empty") with { DurationMs = sw.ElapsedMilliseconds });
            return Task.CompletedTask;
        }

        var totalReplaced = 0;
        var filesProcessed = 0;
        var filesFailed = 0;

        foreach (var glob in SplitGlobs(targetsRaw))
        {
            ct.ThrowIfCancellationRequested();

            var matches = GlobMatcher.Expand(context.WorkingDirectory, glob).ToList();
            if (matches.Count == 0)
            {
                Console.Error.WriteLine(
                    $"::warning::StructuredConfigVariables: glob '{glob}' matched no files under '{context.WorkingDirectory}'. Continuing.");
                continue;
            }

            foreach (var file in matches)
            {
                try
                {
                    // T3 — pre-flight size cap. JsonNode.Parse builds a full
                    // in-memory DOM (typically 3-5x the file size). Reject
                    // oversized inputs so a stray glob match doesn't OOM the agent.
                    if (!EncodingPreservingFileIO.IsWithinSizeLimit(file, out var sizeBytes, out var limitBytes))
                    {
                        Console.Error.WriteLine(
                            $"::warning::StructuredConfigVariables: skipping '{file}' ({sizeBytes:N0} bytes > {limitBytes:N0} byte limit). " +
                            $"Set {EncodingPreservingFileIO.MaxFileSizeMBEnvVar}=<MB> to raise the cap, or refine the target glob.");
                        filesFailed++;
                        continue;
                    }

                    // PR-3: format dispatch. .json / .yaml / .yml / .xml each
                    // route to a dedicated IStructuredConfigFormat. Files whose
                    // extension isn't structured-config (e.g. operator's Targets
                    // glob accidentally matched .txt / .log) are skipped with a
                    // warning — same fail-soft pattern as binary files in G1.1.
                    var format = StructuredConfigFormatRegistry.Resolve(file);
                    if (format is null)
                    {
                        Console.Error.WriteLine(
                            $"::warning::StructuredConfigVariables: skipping '{file}' — extension not recognised. " +
                            $"Supported: {string.Join(", ", StructuredConfigFormatRegistry.SupportedExtensions)}.");
                        filesFailed++;
                        continue;
                    }

                    // BOM preservation — Visual Studio writes appsettings.json
                    // (and many editors XML/YAML files) with a UTF-8 BOM by
                    // default. Round-trip preserves byte signature so rewrites
                    // that change zero leaves don't pollute diffs.
                    var (content, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(file);
                    var result = format.Replace(content, context.Variables);

                    if (!result.Succeeded)
                    {
                        Console.Error.WriteLine(
                            $"::warning::StructuredConfigVariables ({format.FormatName}): skipping '{file}' — {result.FailureReason}");
                        filesFailed++;
                        continue;
                    }

                    if (result.ReplacedCount > 0)
                    {
                        // Atomic write — temp + rename. Half-written file on
                        // `kill -9` would leave operator with corrupt config;
                        // temp+rename keeps original intact until new bytes
                        // are fully on disk. Same primitive G1.2 XDT uses.
                        EncodingPreservingFileIO.WriteAllTextAtomic(file, result.Output, encoding);
                        Console.WriteLine(
                            $"StructuredConfigVariables ({format.FormatName}): '{file}' — {result.ReplacedCount} leaf value(s) replaced.");
                    }

                    filesProcessed++;
                    totalReplaced += result.ReplacedCount;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"::warning::StructuredConfigVariables: failed to process '{file}' — {ex.GetType().Name}: {ex.Message}");
                    filesFailed++;
                }
            }
        }

        Console.WriteLine(
            $"StructuredConfigVariables: processed {filesProcessed} file(s), {filesFailed} failure(s), {totalReplaced} total leaf replacement(s).");

        context.StepOutcomes.Add(StepOutcome.Success(StepName, new Dictionary<string, long>
        {
            ["FilesProcessed"] = filesProcessed,
            ["FilesFailed"] = filesFailed,
            ["LeavesReplaced"] = totalReplaced
        }) with { DurationMs = sw.ElapsedMilliseconds });

        return Task.CompletedTask;
    }

    private static IEnumerable<string> SplitGlobs(string raw)
        => raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
}
