using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — JSON branch of the structured-config format dispatch. Thin
/// adapter over the existing <see cref="JsonPathReplacer"/> (G1.3 +
/// H1-#1 + H1-#4 hardening preserved). The adapter exists so the
/// format-dispatch interface stays clean and YAML/XML siblings live
/// behind the same contract.
/// </summary>
internal sealed class JsonConfigFormat : IStructuredConfigFormat
{
    private static readonly string[] Extensions = { ".json", ".json5" };

    public string FormatName => "JSON";

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public StructuredConfigReplaceResult Replace(string content, VariableSet variables)
    {
        // Delegate to the original replacer — Squid.* self-namespace guard
        // and UnsafeRelaxedJsonEscaping encoder still apply (H1 hardening).
        var result = JsonPathReplacer.Replace(content, variables);
        return result.Succeeded
            ? StructuredConfigReplaceResult.Success(result.Output, result.ReplacedCount)
            : StructuredConfigReplaceResult.Failure(result.FailureReason!);
    }
}
