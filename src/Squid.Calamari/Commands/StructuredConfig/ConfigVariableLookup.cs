using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// Shared variable-lookup contract across every <see cref="IStructuredConfigFormat"/>:
/// JSON / YAML / XML all walk a tree of leaves, compute dot-form paths
/// like <c>Logging.LogLevel.Default</c>, and ask this helper "is there
/// a variable for this path?". Two lookup variants are tried per leaf:
///
/// <list type="bullet">
///   <item><b>Dot form</b> — the canonical path Squid computes
///         (e.g. <c>ConnectionStrings.Default</c>).</item>
///   <item><b>Colon form</b> — same leaf, ASP.NET-Core
///         <c>IConfiguration</c> idiom (<c>Logging:LogLevel:Default</c>).</item>
/// </list>
///
/// <para><b>Self-namespace guard</b> (H1-#1): paths starting with
/// <c>Squid.</c> are skipped in the dot lookup — Squid's runtime
/// emits dot-form internal variables; letting them clobber operator
/// leaves at the same path produces silent corruption. The colon form
/// escape hatch (<c>Squid:X:Y</c>) is still honoured for operators
/// who genuinely need a <c>Squid.*</c>-pathed leaf rewritten.</para>
///
/// <para>Centralised here so JSON / YAML / XML formats can't drift
/// apart on namespace handling — every format uses the same predicate.</para>
/// </summary>
internal static class ConfigVariableLookup
{
    /// <summary>
    /// Try dot-form first (canonical Squid path), then colon-form
    /// (ASP.NET Core IConfiguration idiom). Return first match.
    /// Returns false if neither matches OR if the dot-form starts with
    /// <c>Squid.</c> (self-namespace guard).
    /// </summary>
    public static bool TryFind(VariableSet variables, string dotPath, out string value)
    {
        value = string.Empty;

        // Self-namespace guard — only on DOT form (Squid runtime never
        // emits colon-keyed names; colon form below stays open as the
        // operator-deliberate escape hatch).
        var dotAllowed = !dotPath.StartsWith("Squid.", StringComparison.OrdinalIgnoreCase);

        if (dotAllowed)
        {
            var dot = variables.Get(dotPath);
            if (dot is not null)
            {
                value = dot;
                return true;
            }
        }

        var colonPath = dotPath.Replace('.', ':');
        var colon = variables.Get(colonPath);
        if (colon is not null)
        {
            value = colon;
            return true;
        }

        return false;
    }
}
