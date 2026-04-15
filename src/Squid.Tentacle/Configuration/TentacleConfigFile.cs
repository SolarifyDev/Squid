using System.Text.Json;
using System.Text.Json.Nodes;

namespace Squid.Tentacle.Configuration;

/// <summary>
/// Per-instance persistent config file — a flat JSON document compatible with
/// <c>IConfigurationBuilder.AddJsonFile()</c>. This is what makes
/// <c>register</c> → <c>service install</c> → <c>systemd start</c> actually work:
/// <c>register</c> writes the config, and <c>run</c> (invoked by systemd without
/// any CLI args) reads it back.
/// </summary>
///
/// <remarks>
/// <para>Keys use the same colon-delimited form as <c>appsettings.json</c>
/// (e.g. <c>Tentacle:ServerUrl</c>), written out as nested JSON so the file is
/// both human-readable and directly consumable by
/// <c>ConfigurationBuilder.AddJsonFile()</c>.</para>
/// <para>Precedence when multiple config sources exist (low → high):
/// config file → <c>appsettings.json</c> → env vars → CLI args. This matches
/// Octopus and means temporary overrides via env/CLI still work on top of
/// persisted config.</para>
/// </remarks>
public sealed class TentacleConfigFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Path { get; }

    public TentacleConfigFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public bool Exists() => File.Exists(Path);

    /// <summary>
    /// Reads the file as a flat colon-delimited dictionary. Missing file → empty dictionary.
    /// </summary>
    public Dictionary<string, string> Load()
    {
        if (!File.Exists(Path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(Path);
        var root = JsonNode.Parse(stream);

        var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root is JsonObject obj)
            Flatten(obj, prefix: null, flat);

        return flat;
    }

    /// <summary>
    /// Merges <paramref name="updates"/> into whatever is currently in the file and rewrites it.
    /// Empty/null values in <paramref name="updates"/> are skipped (to avoid wiping out existing keys).
    /// </summary>
    public void Merge(IReadOnlyDictionary<string, string> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var current = Load();

        foreach (var (key, value) in updates)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            current[key] = value;
        }

        Save(current);
    }

    /// <summary>
    /// Overwrites the file with exactly the supplied keys — callers that want
    /// full control over what's persisted use this instead of <see cref="Merge"/>.
    /// </summary>
    public void Save(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var root = new JsonObject();

        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            Nest(root, key.Split(':'), value);

        File.WriteAllText(Path, root.ToJsonString(JsonOptions));

        TryRestrictPermissions(Path);
    }

    /// <summary>Removes a single key (colon-delimited) from the file.</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var current = Load();

        if (!current.Remove(key)) return;

        Save(current);
    }

    private static void Flatten(JsonObject obj, string prefix, Dictionary<string, string> output)
    {
        foreach (var kv in obj)
        {
            var key = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}:{kv.Key}";

            if (kv.Value is JsonObject nested)
                Flatten(nested, key, output);
            else if (kv.Value is not null)
                output[key] = kv.Value.ToString();
        }
    }

    private static void Nest(JsonObject root, string[] segments, string value)
    {
        var cursor = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];

            if (cursor[segment] is not JsonObject next)
            {
                next = new JsonObject();
                cursor[segment] = next;
            }

            cursor = next;
        }

        cursor[segments[^1]] = value;
    }

    /// <summary>
    /// Restricts the config file to owner-read/write only on Unix. Best-effort — silently
    /// skips on Windows (where ACLs handle this) or if the call fails.
    /// </summary>
    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Non-fatal — permissions tightening is a hardening step, not a correctness requirement
        }
    }
}
