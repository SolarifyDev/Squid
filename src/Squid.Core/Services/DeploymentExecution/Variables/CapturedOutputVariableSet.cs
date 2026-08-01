using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Accumulates the output variables a deployment has captured, in the exact shape the
/// checkpoint column stores them: in append order and already protected (sensitive values
/// encrypted) so a checkpoint write never re-encrypts what it wrote before.
///
/// <para><b>What goes in</b>: exactly the entries <see cref="OutputVariableMerger.MergeDetailed"/>
/// reports as APPENDED to the live variable list, verbatim and in order. Nothing is filtered or
/// collapsed here, because the merge's append list already IS the set that went live — and
/// last-wins resolution over a faithful copy of it therefore returns the same value a resumed
/// run would have resolved before the pause. Two earlier attempts got this wrong by trying to
/// re-derive the append set: a membership test over the merged list reports same-value re-emits
/// the merge deliberately skipped, and de-duplicating that result drops a trailing repeat the
/// merge genuinely appended (the merge compares against the FIRST value indexed for a name and
/// never re-indexes, so incoming A,B,C,B appends all four). Both diverged from the live list.</para>
///
/// <para><b>Why entries are stored pre-protected</b>: the checkpoint is rewritten at every batch
/// boundary. Protecting at serialize time re-encrypted the whole accumulated set on every write
/// — at 600k PBKDF2 iterations per value that is hundreds of milliseconds each, and the total
/// work grew quadratically with batch count. Protecting once, on the way in, means each entry is
/// encrypted exactly once for the whole deployment and the serializer passes stored entries
/// straight through via its already-encrypted short-circuit.</para>
///
/// <para>Protection is supplied by the caller as a delegate rather than by taking a dependency on
/// the encryption service, so this type stays free of crypto concerns and is unit-testable
/// against a trivial protector.</para>
///
/// <para>Growth is bounded by the growth of the live variable list itself, which the run already
/// carries — this set never holds an entry the live list does not.</para>
///
/// <para>Not thread-safe: written only from the batch-completion path, which the executor
/// serializes.</para>
/// </summary>
public sealed class CapturedOutputVariableSet
{
    private readonly List<VariableDto> _checkpointReady = new();

    /// <summary>
    /// The captured variables in append order, sensitive values already encrypted. This is
    /// exactly what gets serialized into the checkpoint column.
    /// </summary>
    public IReadOnlyList<VariableDto> CheckpointReady => _checkpointReady;

    public int Count => _checkpointReady.Count;

    /// <summary>
    /// Appends each variable in <paramref name="plaintextVariables"/>, storing the result of
    /// <paramref name="protect"/>.
    ///
    /// <para><paramref name="plaintextVariables"/> must hold PLAINTEXT values —
    /// <paramref name="protect"/> is what turns them into their at-rest form, and applying it to
    /// an already-protected value would double-wrap.</para>
    ///
    /// <para><paramref name="protect"/> runs exactly once per variable appended, which is what
    /// keeps a checkpoint write proportional to entries added since the last write.</para>
    /// </summary>
    /// <returns>The number of variables appended.</returns>
    public int Add(IEnumerable<VariableDto> plaintextVariables, Func<VariableDto, VariableDto> protect)
    {
        ArgumentNullException.ThrowIfNull(protect);

        if (plaintextVariables == null) return 0;

        var added = 0;

        foreach (var variable in plaintextVariables)
        {
            if (variable == null) continue;

            _checkpointReady.Add(protect(variable));
            added++;
        }

        return added;
    }
}
