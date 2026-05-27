using Squid.Calamari.Pipeline;

namespace Squid.Calamari.Commands.Configuration;

/// <summary>
/// Wire-contract constants for the ConfigurationTransforms (XDT) feature.
/// Public so cross-project drift tests in Squid.UnitTests can assert the
/// contract without InternalsVisibleTo pollution.
///
/// <para><b>Canonical vs Legacy</b>: top-level constants are the
/// handler-agnostic canonical wire literals — preferred for new handlers.
/// The nested <see cref="Legacy"/> class holds the IIS-prefixed names that
/// the IIS handler's PS1 script + existing operator deployments emit.
/// <see cref="ConfigurationTransformsStep"/> reads canonical first, falls
/// back to legacy.</para>
/// </summary>
public static class ConfigurationTransformsVariableNames
{
    /// <summary>Canonical, handler-agnostic Enabled toggle.</summary>
    public const string Enabled = "Squid.Action.ConfigurationTransforms.Enabled";

    /// <summary>Canonical environment name driving <c>*.{Env}.config</c> auto-pairs.</summary>
    public const string EnvironmentName = "Squid.Action.ConfigurationTransforms.EnvironmentName";

    /// <summary>Canonical operator-supplied explicit transform pairs
    /// (<c>transform.config =&gt; base.config</c>, newline-separated).</summary>
    public const string AdditionalTransforms = "Squid.Action.ConfigurationTransforms.AdditionalTransforms";

    /// <summary>
    /// Legacy IIS-handler-specific wire literals. Existing operator deployments
    /// + the IIS PS1 script still emit these.
    /// <see cref="ConfigurationTransformsStep"/> falls back to these when the
    /// canonical literals above are not set.
    /// </summary>
    public static class Legacy
    {
        public const string Enabled = "Squid.Action.IISWebSite.ConfigurationTransforms.Enabled";
        public const string EnvironmentName = "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName";
        public const string AdditionalTransforms = "Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms";
    }
}

/// <summary>
/// G1.2 — applies XDT (XML Document Transform) <c>web.{Env}.config</c> style
/// transforms to <c>*.config</c> files in the working directory. Mirrors
/// Octopus's <c>ConfigurationTransformsBehaviour</c>.
///
/// <para><b>Two transform-source modes</b> (both run, additively):
/// <list type="number">
///   <item><b>Auto-pairing by <see cref="ConfigurationTransformsVariableNames.EnvironmentName"/></b>:
///         for each <c>*.config</c> in the working dir, look for a sibling
///         <c>*.{EnvironmentName}.config</c> and apply it. Also unconditionally
///         applies <c>*.Release.config</c> (matches .NET FX build-time XDT
///         defaults — operators rely on this).</item>
///   <item><b>Operator-supplied pairs in
///         <see cref="ConfigurationTransformsVariableNames.AdditionalTransforms"/></b>:
///         newline-separated lines of <c>transform.config => base.config</c>
///         shape. Applied in order. Malformed lines (no <c>=&gt;</c> separator)
///         are skipped with a warning, not fatal.</item>
/// </list></para>
///
/// <para><b>Order in the pipeline</b>: must run AFTER
/// <c>SubstituteInFilesStep</c> so any <c>#{Token}</c> references inside
/// transform files are resolved first. Then XDT applies the now-concrete
/// values to the base config. Matches Octopus's pipeline order.</para>
/// </summary>
internal sealed class ConfigurationTransformsStep : ExecutionStep<RunScriptCommandContext>
{
    public const string StepName = "ConfigurationTransforms";

    public override bool IsEnabled(RunScriptCommandContext context)
    {
        if (context.Variables is null) return false;
        // Canonical first, legacy fallback.
        var raw = context.Variables.Get(ConfigurationTransformsVariableNames.Enabled)
                  ?? context.Variables.Get(ConfigurationTransformsVariableNames.Legacy.Enabled);
        return string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase);
    }

    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                "Working directory has not been initialized — ConfigurationTransformsStep must run after ResolveWorkingDirectoryStep.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                "Variables have not been loaded — ConfigurationTransformsStep must run after LoadVariablesFromFilesStep.");

        if (!Directory.Exists(context.WorkingDirectory))
        {
            Console.WriteLine(
                $"ConfigurationTransforms: working dir '{context.WorkingDirectory}' does not exist; skipping.");
            context.StepOutcomes.Add(StepOutcome.Skipped(StepName, "Working directory does not exist") with { DurationMs = sw.ElapsedMilliseconds });
            return Task.CompletedTask;
        }

        // Canonical first, legacy fallback — same pattern as IsEnabled.
        var environmentName = context.Variables.Get(ConfigurationTransformsVariableNames.EnvironmentName)
                              ?? context.Variables.Get(ConfigurationTransformsVariableNames.Legacy.EnvironmentName);
        var additionalRaw = context.Variables.Get(ConfigurationTransformsVariableNames.AdditionalTransforms)
                            ?? context.Variables.Get(ConfigurationTransformsVariableNames.Legacy.AdditionalTransforms);

        var applied = 0;
        var failed = 0;

        // Auto-pair pass.
        applied += ApplyAutoPairs(context.WorkingDirectory, environmentName, ref failed);

        // Operator-supplied explicit pairs pass.
        if (!string.IsNullOrWhiteSpace(additionalRaw))
            applied += ApplyAdditionalPairs(context.WorkingDirectory, additionalRaw, ref failed);

        Console.WriteLine(
            $"ConfigurationTransforms: applied {applied} transform(s), {failed} failure(s). " +
            "Operator can grep the deploy log for 'ConfigurationTransforms:' lines to see per-pair outcomes.");

        context.StepOutcomes.Add(StepOutcome.Success(StepName, new Dictionary<string, long>
        {
            ["TransformsApplied"] = applied,
            ["TransformsFailed"] = failed
        }) with { DurationMs = sw.ElapsedMilliseconds });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Find each <c>*.config</c> in the working dir + look for matching
    /// <c>*.{Env}.config</c> AND <c>*.Release.config</c> siblings. Both get
    /// applied to the base. .NET FX build-time XDT semantic.
    /// </summary>
    private static int ApplyAutoPairs(string workingDir, string? environmentName, ref int failed)
    {
        var applied = 0;

        foreach (var baseConfig in EnumerateBaseConfigs(workingDir))
        {
            var baseName = Path.GetFileNameWithoutExtension(baseConfig);
            var baseDir = Path.GetDirectoryName(baseConfig)!;

            // Release.config — always applied (matches .NET FX build-time default).
            var releaseTransform = Path.Combine(baseDir, $"{baseName}.Release.config");
            if (File.Exists(releaseTransform))
                applied += TryApply(releaseTransform, baseConfig, ref failed);

            // EnvironmentName-specific transform.
            if (!string.IsNullOrWhiteSpace(environmentName))
            {
                var envTransform = Path.Combine(baseDir, $"{baseName}.{environmentName}.config");
                if (File.Exists(envTransform) && !string.Equals(envTransform, releaseTransform, StringComparison.OrdinalIgnoreCase))
                    applied += TryApply(envTransform, baseConfig, ref failed);
            }
        }

        return applied;
    }

    /// <summary>
    /// Operator's explicit transform list — newline-separated lines of shape
    /// <c>transform.config =&gt; base.config</c>. Paths are relative to
    /// working dir.
    /// </summary>
    private static int ApplyAdditionalPairs(string workingDir, string raw, ref int failed)
    {
        var applied = 0;

        foreach (var line in SplitLines(raw))
        {
            var (transform, baseFile, ok) = ParsePair(line);
            if (!ok)
            {
                Console.Error.WriteLine(
                    $"::warning::ConfigurationTransforms: malformed AdditionalTransforms line '{line}' — expected 'transform.config => base.config' shape. Skipping this line, continuing with the rest.");
                continue;
            }

            var transformPath = Path.Combine(workingDir, transform);
            var basePath = Path.Combine(workingDir, baseFile);

            applied += TryApply(transformPath, basePath, ref failed);
        }

        return applied;
    }

    private static int TryApply(string transformPath, string basePath, ref int failed)
    {
        var result = XdtTransformer.Transform(basePath, transformPath);

        if (result.Succeeded)
        {
            Console.WriteLine($"ConfigurationTransforms: applied '{transformPath}' → '{basePath}'.");
            return 1;
        }

        Console.Error.WriteLine($"::warning::ConfigurationTransforms: {result.FailureReason}");
        failed++;
        return 0;
    }

    /// <summary>
    /// Enumerate <c>*.config</c> files that are BASE files, not transform
    /// sources. Skip <c>*.Release.config</c> + <c>*.Debug.config</c> +
    /// <c>*.{Env}.config</c>-shape files. Heuristic: a base file's stem has
    /// no second dot (e.g. <c>web</c>), a transform's stem does
    /// (e.g. <c>web.Production</c>).
    /// </summary>
    private static IEnumerable<string> EnumerateBaseConfigs(string workingDir)
    {
        IEnumerable<string> allConfigs;
        try
        {
            allConfigs = Directory.EnumerateFiles(workingDir, "*.config", SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }

        return allConfigs.Where(IsBaseConfig);
    }

    private static bool IsBaseConfig(string path)
    {
        // Stem with no dots = base. Stem with at least one dot = transform.
        // `web.config`  → stem `web`           → base ✓
        // `web.Production.config` → stem `web.Production` → transform (has dot)
        var fileName = Path.GetFileNameWithoutExtension(path);
        return !fileName.Contains('.');
    }

    private static (string transform, string baseFile, bool ok) ParsePair(string line)
    {
        var idx = line.IndexOf("=>", StringComparison.Ordinal);
        if (idx < 0) return ("", "", false);

        var transform = line[..idx].Trim();
        var baseFile = line[(idx + 2)..].Trim();
        if (transform.Length == 0 || baseFile.Length == 0) return ("", "", false);

        return (transform, baseFile, true);
    }

    private static IEnumerable<string> SplitLines(string raw)
        => raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
}
