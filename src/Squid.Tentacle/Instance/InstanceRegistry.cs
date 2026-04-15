using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Instance;

/// <summary>
/// Persisted list of all Tentacle instances on this host, stored as
/// <c>{configDir}/instances.json</c>.
/// </summary>
///
/// <remarks>
/// <para>Analogue to Octopus Tentacle's Windows Registry "Software\Octopus"
/// instance registry — but cross-platform via plain JSON, so it works
/// identically on Linux/macOS/Windows.</para>
/// <para>Instances are stored in system-scope if the current process can write
/// there (root/Administrator); otherwise user-scope. Reads prefer system-scope
/// when both exist.</para>
/// </remarks>
public sealed class InstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configDir;

    public InstanceRegistry(string configDir)
    {
        _configDir = configDir;
    }

    /// <summary>
    /// Creates a registry bound to the default writable config dir for the current process.
    /// </summary>
    public static InstanceRegistry CreateForCurrentProcess() =>
        new(PlatformPaths.PickWritableConfigDir());

    /// <summary>
    /// Creates a registry bound to the active (readable) config dir. Use for lookups
    /// from read-only contexts like <c>run</c>.
    /// </summary>
    public static InstanceRegistry CreateForRead() =>
        new(PlatformPaths.ResolveActiveConfigDir());

    public string Path => PlatformPaths.GetInstancesRegistryPath(_configDir);

    public IReadOnlyList<InstanceRecord> List()
    {
        if (!File.Exists(Path))
            return Array.Empty<InstanceRecord>();

        var json = File.ReadAllText(Path);
        var data = JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions);

        return (IReadOnlyList<InstanceRecord>)data?.Instances ?? Array.Empty<InstanceRecord>();
    }

    public InstanceRecord Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return List().FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the record for <paramref name="name"/>, or creates a default-shaped one
    /// (but does NOT persist it). The caller decides whether to persist via <see cref="Add"/>.
    /// </summary>
    public InstanceRecord FindOrDefault(string name)
    {
        var existing = Find(name);

        if (existing != null)
            return existing;

        return new InstanceRecord
        {
            Name = name,
            ConfigPath = PlatformPaths.GetInstanceConfigPath(_configDir, name)
        };
    }

    public void Add(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Name);

        var file = LoadFile();

        if (file.Instances.Any(i => i.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Instance '{record.Name}' already exists");

        if (string.IsNullOrWhiteSpace(record.ConfigPath))
            record.ConfigPath = PlatformPaths.GetInstanceConfigPath(_configDir, record.Name);

        file.Instances.Add(record);
        SaveFile(file);
    }

    public void Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var file = LoadFile();
        var removed = file.Instances.RemoveAll(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0) return;

        SaveFile(file);
    }

    /// <summary>
    /// Ensures the <c>Default</c> instance exists, creating it on-disk if not.
    /// This is the "zero-config" escape hatch for users who invoke register/run
    /// without first calling <c>create-instance</c>.
    /// </summary>
    public InstanceRecord EnsureDefault()
    {
        var existing = Find(InstanceRecord.DefaultName);

        if (existing != null)
            return existing;

        var record = new InstanceRecord
        {
            Name = InstanceRecord.DefaultName,
            ConfigPath = PlatformPaths.GetInstanceConfigPath(_configDir, InstanceRecord.DefaultName)
        };

        Add(record);
        return record;
    }

    private RegistryFile LoadFile()
    {
        if (!File.Exists(Path))
            return new RegistryFile();

        var json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions) ?? new RegistryFile();
    }

    private void SaveFile(RegistryFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(file, JsonOptions));
    }

    /// <summary>Root JSON shape: <c>{ "version": 1, "instances": [...] }</c>.</summary>
    private sealed class RegistryFile
    {
        public int Version { get; set; } = 1;
        public List<InstanceRecord> Instances { get; set; } = new();
    }
}
