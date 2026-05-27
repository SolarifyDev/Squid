using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// Per-format leaf-value replacer for structured-configuration files
/// (JSON / YAML / XML). The <see cref="StructuredConfigVariablesStep"/>
/// dispatches per file via <see cref="StructuredConfigFormatRegistry"/> —
/// one format-specific implementation handles each extension. All formats
/// share the same semantic: walk the doc tree, compute the dot/colon-path
/// per leaf, look up the variable, replace if matched.
///
/// <para><b>Why per-format classes</b>: JSON's <c>System.Text.Json.Nodes</c>,
/// YAML's <c>YamlStream</c>, and XML's <c>XDocument</c> have meaningfully
/// different DOM shapes. Type-preservation rules differ too (YAML scalar
/// style, XML attribute vs element). Each format's logic is sufficiently
/// distinct that a unified DOM abstraction would obscure more than it
/// would share.</para>
///
/// <para><b>Self-namespace guard contract</b>: every format MUST skip
/// variables starting with <c>Squid.</c> in dot form — Squid runtime
/// variables (release id, deployment guid, …) MUST NOT clobber operator
/// leaves at the same path. The colon-form escape hatch from H1-#1 still
/// applies. JSON already has this via <see cref="JsonPathReplacer"/>;
/// YAML + XML formats inherit the same contract through the shared
/// <see cref="ConfigVariableLookup"/> helper.</para>
/// </summary>
internal interface IStructuredConfigFormat
{
    /// <summary>Operator-visible name for log lines ("JSON", "YAML", "XML").</summary>
    string FormatName { get; }

    /// <summary>
    /// True when this format owns the file based on extension. Caller
    /// uses the first match — the registry orders formats so the most-
    /// specific extension wins.
    /// </summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Walk the document, replace leaves whose computed path matches a
    /// non-Squid-namespaced variable, return the new content + count.
    /// Returns <see cref="StructuredConfigReplaceResult.Failure"/> for
    /// malformed input — caller logs + skips the file.
    /// </summary>
    StructuredConfigReplaceResult Replace(string content, VariableSet variables);
}

/// <summary>
/// Format-agnostic result of a structured-config replacement pass.
/// </summary>
internal sealed record StructuredConfigReplaceResult(
    bool Succeeded,
    string Output,
    int ReplacedCount,
    string? FailureReason)
{
    public static StructuredConfigReplaceResult Success(string output, int replacedCount)
        => new(true, output, replacedCount, null);

    public static StructuredConfigReplaceResult Failure(string reason)
        => new(false, string.Empty, 0, reason);
}
