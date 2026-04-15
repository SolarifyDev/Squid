using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Instance;

/// <summary>
/// Turns the user's <c>--instance NAME</c> argument (or its absence) into a
/// concrete <see cref="InstanceRecord"/> that callers can use to locate certs
/// and config files.
/// </summary>
///
/// <remarks>
/// Behaviour:
/// <list type="bullet">
/// <item>Named instance resolved from <see cref="InstanceRegistry"/>. Missing → throws.</item>
/// <item>No name + <c>Default</c> is registered → that record.</item>
/// <item>No name + no <c>Default</c> → synthesised record pointing at conventional paths
/// (no filesystem side-effects — callers that need persistence call
/// <see cref="InstanceRegistry.EnsureDefault"/> themselves).</item>
/// </list>
/// </remarks>
public static class InstanceSelector
{
    /// <summary>
    /// Extracts the <c>--instance NAME</c> argument from a CLI arg array, returning
    /// the name plus the original args with that pair stripped out so downstream
    /// <c>AddCommandLine(...)</c> doesn't see an unknown option.
    /// </summary>
    public static (string Name, string[] Remaining) ExtractInstanceArg(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--instance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var remaining = args.Take(i).Concat(args.Skip(i + 2)).ToArray();
                return (args[i + 1], remaining);
            }

            // Also support --instance=NAME form
            if (args[i].StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
            {
                var remaining = args.Take(i).Concat(args.Skip(i + 1)).ToArray();
                return (args[i]["--instance=".Length..], remaining);
            }
        }

        return (null, args);
    }

    /// <summary>
    /// Resolves the instance specified by <paramref name="instanceName"/> (null → Default).
    /// Read-only — does not create or mutate the registry.
    /// </summary>
    public static InstanceRecord Resolve(string instanceName)
    {
        var registry = InstanceRegistry.CreateForRead();
        var effectiveName = string.IsNullOrWhiteSpace(instanceName) ? InstanceRecord.DefaultName : instanceName;

        var record = registry.Find(effectiveName);

        if (record != null) return record;

        // Default always resolves, even if the registry is empty — so first-run
        // commands (register etc.) can still locate the standard paths.
        if (effectiveName.Equals(InstanceRecord.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            var configDir = PlatformPaths.ResolveActiveConfigDir();

            return new InstanceRecord
            {
                Name = InstanceRecord.DefaultName,
                ConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceRecord.DefaultName)
            };
        }

        throw new InvalidOperationException(
            $"Instance '{effectiveName}' does not exist. Run 'squid-tentacle create-instance --instance {effectiveName}' first, " +
            $"or pass --instance Default (or omit the flag) to use the default instance.");
    }

    /// <summary>
    /// Returns the per-instance certs directory (<c>{configDir}/instances/{name}/certs</c>).
    /// Ensures multi-instance setups don't collide on certificate storage.
    /// </summary>
    public static string ResolveCertsPath(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var configDir = PlatformPaths.ResolveActiveConfigDir();
        return PlatformPaths.GetInstanceCertsDir(configDir, record.Name);
    }
}
