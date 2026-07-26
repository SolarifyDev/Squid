using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Accumulates the output variables a deployment has captured, in the exact shape the
/// checkpoint column stores them: de-duplicated, in first-appearance order, and already
/// protected (sensitive values encrypted) so a checkpoint write never re-encrypts.
///
/// <para><b>Why de-duplication is required for correctness, not just size</b>: a step that
/// runs across N targets emits the same output variable once per target, and
/// <see cref="OutputVariableMerger.SelectAccepted"/> reports every one of those emits as
/// accepted (a same-value re-emit IS present in the merged set). Appending them verbatim
/// grows the checkpoint by one duplicate per target per pause/resume cycle, without bound.
/// <see cref="OutputVariableMerger.Merge"/> itself skips a same-value re-emit
/// (<c>prior.Value == v.Value</c> → <c>continue</c>) and never appends it, so de-duplicating
/// on the full (name, value, sensitivity) triple reproduces the live merged list EXACTLY —
/// including the deliberate Warn/Off behaviour of keeping two entries for one name when the
/// values genuinely differ. Restoring this set therefore resolves to the same value the live
/// run resolved, under any precedence rule the variable dictionary applies.</para>
///
/// <para><b>Why entries are stored pre-protected</b>: the checkpoint is rewritten at every
/// batch boundary. Protecting at serialize time re-encrypted the whole accumulated set on
/// every write — at 600k PBKDF2 iterations per value that is hundreds of milliseconds each,
/// and the total work grows quadratically with batch count. Protecting once, at capture,
/// makes each write O(entries added since the last write); the serializer's
/// already-encrypted short-circuit then passes stored entries straight through.</para>
///
/// <para>Protection is supplied by the caller as a delegate rather than by taking a
/// dependency on the encryption service, so this type stays free of crypto concerns and can
/// be unit-tested against a trivial protector.</para>
///
/// <para>Not thread-safe: it is written only from the batch-completion path, which is
/// serialized by the executor.</para>
/// </summary>
public sealed class CapturedOutputVariableSet
{
    private readonly List<VariableDto> _checkpointReady = new();
    private readonly HashSet<Identity> _seen = new();

    /// <summary>
    /// The captured variables in first-appearance order, sensitive values already encrypted.
    /// This is exactly what gets serialized into the checkpoint column.
    /// </summary>
    public IReadOnlyList<VariableDto> CheckpointReady => _checkpointReady;

    public int Count => _checkpointReady.Count;

    /// <summary>
    /// Adds each variable in <paramref name="plaintextVariables"/> that is not already held,
    /// storing the result of <paramref name="protect"/> for the ones it keeps.
    ///
    /// <para><paramref name="plaintextVariables"/> must hold PLAINTEXT values — identity is
    /// computed before protection because ciphertext is non-deterministic (a fresh random
    /// salt per payload), so two encryptions of one value would never compare equal.</para>
    ///
    /// <para><paramref name="protect"/> is invoked only for variables actually added, which is
    /// what keeps checkpoint cost proportional to new entries rather than to total entries.</para>
    /// </summary>
    /// <returns>The number of variables added.</returns>
    public int Add(IEnumerable<VariableDto> plaintextVariables, Func<VariableDto, VariableDto> protect)
    {
        ArgumentNullException.ThrowIfNull(protect);

        if (plaintextVariables == null) return 0;

        var added = 0;

        foreach (var variable in plaintextVariables)
        {
            if (variable == null) continue;

            if (!_seen.Add(Identity.For(variable))) continue;

            _checkpointReady.Add(protect(variable));
            added++;
        }

        return added;
    }

    /// <summary>
    /// Identity of a captured output variable, matching <see cref="OutputVariableMerger.Merge"/>'s
    /// comparison rules exactly: names compare case-insensitively (Merge indexes them with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>) while values compare ordinally (Merge's
    /// same-value test uses <see cref="StringComparison.Ordinal"/>). Sensitivity participates so
    /// a value that changed classification is never silently collapsed into the earlier entry.
    /// </summary>
    private readonly record struct Identity(string Name, string Value, bool IsSensitive)
    {
        public static Identity For(VariableDto variable)
            => new(variable.Name ?? string.Empty, variable.Value ?? string.Empty, variable.IsSensitive);

        public bool Equals(Identity other)
            => IsSensitive == other.IsSensitive
               && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override int GetHashCode()
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Value, IsSensitive);
    }
}
