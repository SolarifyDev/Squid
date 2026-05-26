using System.Text;
using System.Text.RegularExpressions;

namespace Squid.Calamari.Commands.Substitution;

/// <summary>
/// G1.1 — minimal in-house glob-to-file-enumeration utility. Backs
/// <see cref="SubstituteInFilesStep"/>'s target-files matching. Deliberately
/// avoids the <c>Microsoft.Extensions.FileSystemGlobbing</c> NuGet because
/// Squid.Calamari ships in the agent zip and we want the smallest possible
/// agent footprint (per the Architecture Decision — Squid.Calamari stays
/// lean, no heavy plugin distribution).
///
/// <para><b>Supported patterns</b> (operator-facing — matches Octopus's
/// <c>Octopus.Action.SubstituteInFiles.TargetFiles</c> shape):
/// <list type="bullet">
///   <item><c>web.config</c> — literal filename, exact match in root</item>
///   <item><c>*.config</c> — wildcard within a single segment</item>
///   <item><c>**/*.config</c> — recursive across subdirectories</item>
///   <item><c>config/*.json</c> — sub-segment wildcard</item>
///   <item><c>**/appsettings.*.json</c> — recurse + wildcard suffix</item>
/// </list></para>
///
/// <para><b>Anti-injection</b>: dots in glob patterns are literal, not
/// regex any-char. Path-traversal globs (<c>../*.config</c>) yield zero
/// matches — substitution stays sandboxed to the working dir. Pinned by
/// <c>Expand_DotIsLiteral_NotRegexAnyChar</c> and
/// <c>Expand_PathTraversalAttempt_BoundedToRoot</c>.</para>
/// </summary>
internal static class GlobMatcher
{
    public static IEnumerable<string> Expand(string rootDir, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return Array.Empty<string>();
        if (string.IsNullOrEmpty(rootDir)) return Array.Empty<string>();
        if (!Directory.Exists(rootDir)) return Array.Empty<string>();

        // Path-traversal sandbox: reject any pattern that uses `..` segments.
        // No legitimate config-substitution glob needs to escape the working
        // dir; a malicious package could otherwise use this to rewrite host
        // files outside the deploy work area.
        var normalised = pattern.Replace('\\', '/');
        if (normalised.Split('/').Any(seg => seg == ".."))
            return Array.Empty<string>();

        var regex = GlobToRegex(normalised);
        var rootFull = Path.GetFullPath(rootDir);

        return EnumerateAllFiles(rootDir)
            .Select(absPath => (absPath, relPath: GetRelativePath(rootFull, absPath)))
            .Where(t => regex.IsMatch(t.relPath))
            .Select(t => t.absPath)
            .ToList();    // materialise so caller can mutate files without enumeration races
    }

    private static IEnumerable<string> EnumerateAllFiles(string rootDir)
    {
        try
        {
            return Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't read — log? for now, silent skip;
            // the SubstituteInFilesStep logs the overall outcome.
            return Array.Empty<string>();
        }
    }

    private static string GetRelativePath(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace('\\', '/');    // normalise so glob patterns are platform-agnostic
    }

    /// <summary>
    /// Translate a glob pattern into a regex. Pure function — pinned by
    /// <c>GlobMatcherTests</c>.
    /// </summary>
    internal static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");

        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // `**` = match anything including path separators (recursive).
                // Skip optional trailing slash so `**/*.config` works.
                sb.Append(".*");
                i += 2;
                if (i < pattern.Length && pattern[i] == '/') i++;
            }
            else if (c == '*')
            {
                // `*` = match any char EXCEPT path separator (within-segment).
                sb.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                // `?` = single non-separator char.
                sb.Append("[^/]");
                i++;
            }
            else
            {
                // Everything else (including `.`) is literal — escape for regex.
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
