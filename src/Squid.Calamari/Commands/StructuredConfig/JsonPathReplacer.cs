using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// G1.3 — structure-aware JSON-leaf replacement. For each leaf in the input
/// JSON, check if its computed path matches any variable name in the
/// supplied <see cref="VariableSet"/>; if so, replace the leaf value with
/// the variable's value.
///
/// <para><b>Path forms accepted</b> (both lookup styles for the same leaf):
/// <list type="bullet">
///   <item>Dot-separated: <c>ConnectionStrings.Default</c></item>
///   <item>Colon-separated: <c>Logging:LogLevel:Default</c>
///         (ASP.NET Core IConfiguration idiom)</item>
/// </list>
/// At each leaf the replacer asks both forms; the variable set is checked
/// case-insensitively (operator inputs may be inconsistent across Octopus
/// imports vs. native Squid).</para>
///
/// <para><b>Type preservation</b>: leaf value JSON type is preserved across
/// replacement. A leaf of type number stays a number if the variable value
/// parses; falls back to string if not. Booleans similarly. Null leaves
/// become string-quoted on replacement (operators almost always mean a
/// real value at that point).</para>
///
/// <para><b>Non-leaf paths are silently skipped</b>: a variable matching an
/// object/array node would corrupt the schema if we replaced it with a
/// scalar string. Skip + log.</para>
///
/// <para><b>Walked once, not per-variable</b>: O(json_leaves) instead of
/// O(variables × leaves). Most Squid variable sets have 50-200 entries;
/// most JSON files have &lt;100 leaves. Per-variable iteration would be
/// 10,000+ lookups per file; per-leaf is 100.</para>
/// </summary>
internal static class JsonPathReplacer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Default encoder escapes `<`, `>`, `&`, `+` into `\uXXXX` for HTML safety.
        // That mangles operator URL strings and any descriptive text containing
        // those characters — appsettings.json diff after a rewrite looks like
        // gibberish to the operator (even though .NET IConfiguration parses it
        // correctly). UnsafeRelaxedJsonEscaping keeps those characters readable.
        // The "Unsafe" prefix only matters for HTML-embedded JSON; appsettings.json
        // is consumed by IConfiguration, never injected into HTML.
        // Pinned by Replace_OperatorStringContainsUrlSpecialChars_StaysReadable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        // Operators sometimes leave commas / comments in appsettings.json
        // during development. Be tolerant so we don't fail a deploy over
        // dev-era artifacts.
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonReplaceResult Replace(string json, VariableSet variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (string.IsNullOrWhiteSpace(json)) return JsonReplaceResult.Success(json ?? string.Empty, 0);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ParseOptions);
        }
        catch (JsonException ex)
        {
            return JsonReplaceResult.Failure($"Failed to parse JSON: {ex.Message}");
        }

        if (root is null)
            return JsonReplaceResult.Success(json, 0);

        var replacedCount = 0;
        ReplaceRecursive(root, currentPath: string.Empty, variables, ref replacedCount);

        return JsonReplaceResult.Success(root.ToJsonString(WriteOptions), replacedCount);
    }

    private static void ReplaceRecursive(JsonNode node, string currentPath, VariableSet variables, ref int replacedCount)
    {
        switch (node)
        {
            case JsonObject obj:
                // Materialise list of properties — we may rewrite their values
                // mid-iteration which would otherwise invalidate the enumerator.
                foreach (var prop in obj.ToList())
                {
                    var childPath = currentPath.Length == 0 ? prop.Key : $"{currentPath}.{prop.Key}";

                    if (prop.Value is null)
                    {
                        // Null leaf — try to replace with variable value as string.
                        if (TryFindVariable(variables, childPath, out var newValue))
                        {
                            obj[prop.Key] = JsonValue.Create(newValue);
                            replacedCount++;
                        }
                        continue;
                    }

                    if (prop.Value is JsonValue leaf)
                    {
                        if (TryFindVariable(variables, childPath, out var newValue))
                        {
                            obj[prop.Key] = CoerceToOriginalType(leaf, newValue);
                            replacedCount++;
                        }
                    }
                    else
                    {
                        // Recurse into nested object / array.
                        ReplaceRecursive(prop.Value, childPath, variables, ref replacedCount);
                    }
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var childPath = $"{currentPath}.{i}";

                    if (arr[i] is null)
                    {
                        if (TryFindVariable(variables, childPath, out var newValue))
                        {
                            arr[i] = JsonValue.Create(newValue);
                            replacedCount++;
                        }
                        continue;
                    }

                    if (arr[i] is JsonValue leaf)
                    {
                        if (TryFindVariable(variables, childPath, out var newValue))
                        {
                            arr[i] = CoerceToOriginalType(leaf, newValue);
                            replacedCount++;
                        }
                    }
                    else
                    {
                        ReplaceRecursive(arr[i]!, childPath, variables, ref replacedCount);
                    }
                }
                break;

            // JsonValue at root = scalar JSON file (e.g. just "hello"). Rare;
            // no path to match against. No-op.
        }
    }

    /// <summary>
    /// Try the dot-form first (the canonical computed path), then the
    /// colon-form (ASP.NET Core IConfiguration idiom — same leaf, different
    /// separator). Returns the first match.
    ///
    /// <para><b>Self-namespace guard</b>: paths starting with <c>Squid.</c>
    /// are skipped — Squid's own internal variables (e.g. step toggles,
    /// release metadata, deployment ids) carry implementation details, and
    /// letting them clobber an operator's JSON leaf at the same path
    /// produces surprise corruption with no operator-visible cause.
    /// Operators targeting a JSON path that genuinely begins with
    /// <c>Squid.</c> can still hit it via the colon form
    /// (<c>Squid:Some:Path</c>) — that encodes explicit intent and is
    /// allowed.</para>
    /// </summary>
    private static bool TryFindVariable(VariableSet variables, string dotPath, out string value)
    {
        value = string.Empty;

        // Self-namespace guard — only on the DOT form (which is what Squid's
        // own runtime emits for its internal variables, e.g.
        // `Squid.Action.IISWebSite.Foo.Bar`, `Squid.Deployment.Id`).
        // Squid's runtime never emits colon-keyed names, so a colon-form
        // variable with `Squid:...` IS operator-deliberate intent and is
        // allowed through below. Pinned by
        // Replace_SquidNamespacedVariable_DoesNotClobberJsonLeaf +
        // Replace_SquidNamespacedVariable_OperatorCanStillForceWithColonForm.
        var dotFormAllowed = !dotPath.StartsWith("Squid.", StringComparison.OrdinalIgnoreCase);

        if (dotFormAllowed)
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

    /// <summary>
    /// Preserve the leaf's JSON type when substituting. A boolean leaf
    /// stays boolean if the new value parses; a number leaf stays numeric;
    /// strings stay strings. Fallback to string when the new value isn't
    /// parseable in the original type — operator might intentionally want
    /// the type override (rare but legitimate).
    /// </summary>
    private static JsonNode? CoerceToOriginalType(JsonValue originalLeaf, string newValue)
    {
        var element = originalLeaf.GetValue<JsonElement>();

        switch (element.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                if (bool.TryParse(newValue, out var boolVal))
                    return JsonValue.Create(boolVal);
                return JsonValue.Create(newValue);

            case JsonValueKind.Number:
                if (long.TryParse(newValue, out var longVal))
                    return JsonValue.Create(longVal);
                if (double.TryParse(newValue, out var doubleVal))
                    return JsonValue.Create(doubleVal);
                return JsonValue.Create(newValue);

            default:
                return JsonValue.Create(newValue);
        }
    }
}

internal sealed record JsonReplaceResult(bool Succeeded, string Output, int ReplacedCount, string? FailureReason)
{
    public static JsonReplaceResult Success(string output, int replacedCount)
        => new(true, output, replacedCount, null);

    public static JsonReplaceResult Failure(string reason)
        => new(false, string.Empty, 0, reason);
}
