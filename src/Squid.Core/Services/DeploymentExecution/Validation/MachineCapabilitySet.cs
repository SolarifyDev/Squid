using System.Collections.Immutable;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Projects the per-machine runtime capabilities (collected by the agent and
/// stored in <see cref="MachineRuntimeCapabilities"/>) into the <c>slot →
/// values</c> shape consumed by <see cref="CapabilityValidator"/>'s
/// requirement-matching loop.
///
/// <para>This is the "target half" of the static-requirement matching contract.
/// The handler half is <c>IActionHandler.StaticRequirements</c>. Matching
/// succeeds when, for every slot the handler requires, the projected machine
/// map advertises AT LEAST ONE value in the handler's acceptable-values set.</para>
///
/// <para><b>OS-string tolerance</b>: the agent's OS metadata may be one of two
/// forms — the canonical short string (<c>"Windows"</c>) from the current
/// <c>RuntimeCapabilitiesInspector.DetectOs()</c>, or the legacy long form
/// <c>"Microsoft Windows NT 10.0.19045.0"</c> from older Tentacle binaries that
/// wrote <c>Environment.OSVersion.VersionString</c> directly into the "os" key.
/// This projection normalises both into <see cref="CapabilityKeys.Os.Windows"/>
/// so handler requirements can be written against the canonical taxonomy
/// without worrying about agent-side history.</para>
/// </summary>
public static class MachineCapabilitySet
{
    /// <summary>
    /// Projects the supplied capabilities into the slot map. Returns an empty
    /// map when <paramref name="caps"/> is null OR has no usable signal —
    /// equivalent to "unknown target", which the validator treats as
    /// optimistic-allow rather than reject.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> From(MachineRuntimeCapabilities caps)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        ProjectOs(caps?.Os, builder);
        ProjectArchitecture(caps?.Architecture, builder);
        ProjectShells(caps?.InstalledShells, builder);

        return builder.ToImmutable();
    }

    private static void ProjectOs(string osValue, ImmutableDictionary<string, IReadOnlySet<string>>.Builder builder)
    {
        if (string.IsNullOrWhiteSpace(osValue)) return;

        // Tolerance for two real-world forms — see class-level doc-comment.
        if (IsWindows(osValue))
        {
            builder.Add(CapabilityKeys.OsSlot, ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase, CapabilityKeys.Os.Windows));
            return;
        }

        if (string.Equals(osValue, AgentOperatingSystems.Linux, StringComparison.OrdinalIgnoreCase))
        {
            builder.Add(CapabilityKeys.OsSlot, ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase, CapabilityKeys.Os.Linux));
            return;
        }

        if (string.Equals(osValue, AgentOperatingSystems.MacOS, StringComparison.OrdinalIgnoreCase))
            builder.Add(CapabilityKeys.OsSlot, ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase, CapabilityKeys.Os.MacOS));

        // AgentOperatingSystems.Unknown / unrecognised strings → don't project anything.
        // Validator treats "slot absent from target" as "unknown", which is permissive by design
        // (avoid rejecting a target whose agent hasn't health-checked yet).
    }

    private static void ProjectArchitecture(string arch, ImmutableDictionary<string, IReadOnlySet<string>>.Builder builder)
    {
        if (string.IsNullOrWhiteSpace(arch)) return;

        // .NET RuntimeInformation.ProcessArchitecture.ToString() returns "X64" / "Arm64" / "X86" / "Arm".
        // Normalise to lowercase for matching.
        var normalized = arch.ToLowerInvariant();

        builder.Add(CapabilityKeys.ArchSlot,
            ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase, normalized));
    }

    private static void ProjectShells(string installedShells, ImmutableDictionary<string, IReadOnlySet<string>>.Builder builder)
    {
        if (string.IsNullOrWhiteSpace(installedShells)) return;

        // Each shell on PATH becomes its OWN slot with value Present, so handlers
        // can AND-require multiple shells. The shell names from the agent inspector
        // are already lowercase (pwsh / powershell / bash / cmd / sh).
        var presentSet = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase, CapabilityKeys.Present);

        foreach (var raw in installedShells.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var shellSlot = $"shell:{raw.ToLowerInvariant()}";
            builder[shellSlot] = presentSet;
        }
    }

    /// <summary>
    /// Returns true when the OS string identifies a Windows host. Accepts the
    /// canonical short form (<see cref="AgentOperatingSystems.Windows"/>) AND
    /// the legacy long form starting with <c>"Microsoft Windows"</c> (from
    /// older Tentacle binaries that wrote <c>Environment.OSVersion.VersionString</c>).
    ///
    /// <para>Anchored on <c>StartsWith("Microsoft Windows")</c> not
    /// <c>Contains("Windows")</c> so unrelated strings like
    /// <c>"LinuxOnWindowsSubsystem"</c> don't false-positive.</para>
    /// </summary>
    internal static bool IsWindows(string osValue)
    {
        if (string.IsNullOrWhiteSpace(osValue)) return false;

        if (string.Equals(osValue, AgentOperatingSystems.Windows, StringComparison.OrdinalIgnoreCase))
            return true;

        return osValue.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
    }
}
