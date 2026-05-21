using System.Collections.Immutable;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Fluent builder + canonical empty for the <c>slot → acceptable-values</c> map
/// used by <see cref="Handlers.IActionHandler.StaticRequirements"/> and the
/// matching surface advertised by <c>MachineCapabilitySet</c>.
///
/// <para>Backed by <see cref="ImmutableDictionary{TKey,TValue}"/> so handler
/// instances can declare a single shared instance as a static / property
/// initialiser without defensive copying. Case-insensitive string comparison
/// at both the slot and value level.</para>
///
/// <para><b>Example</b> (IIS deploy handler):
/// <code>
/// public IReadOnlyDictionary&lt;string, IReadOnlySet&lt;string&gt;&gt; StaticRequirements { get; } =
///     CapabilityRequirements.Empty
///         .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
///         .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);
/// </code></para>
/// </summary>
public static class CapabilityRequirements
{
    /// <summary>Canonical empty map — handlers that don't declare any static requirements use this.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Empty
        = ImmutableDictionary<string, IReadOnlySet<string>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a slot with the given acceptable values to the requirements map.
    /// AND across slots, OR within a slot — calling Require twice with the
    /// same slot REPLACES (not unions) the previous values, mirroring
    /// dictionary semantics.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Require(
        this IReadOnlyDictionary<string, IReadOnlySet<string>> existing,
        string slot,
        params string[] acceptableValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ArgumentNullException.ThrowIfNull(acceptableValues);

        if (acceptableValues.Length == 0)
            throw new ArgumentException(
                $"Require('{slot}', ...) was called with no acceptable values. A slot with zero acceptable values can never be satisfied; remove the call instead.",
                nameof(acceptableValues));

        var values = ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, acceptableValues);

        if (existing is ImmutableDictionary<string, IReadOnlySet<string>> immutable)
            return immutable.SetItem(slot, values);

        return existing.Aggregate(
            ImmutableDictionary<string, IReadOnlySet<string>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            (acc, kvp) => acc.SetItem(kvp.Key, kvp.Value)).SetItem(slot, values);
    }
}
