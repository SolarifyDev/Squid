using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variables;

/// <summary>
/// Single source of truth for "which sensitive variable of a set is meant, and can it still be filled": exactly one slot with the
/// identity, and only while it is empty. Read paths (preview) and the compare-and-set (confirmation) both classify through it.
/// </summary>
public static class SensitiveVariableSlots
{
    public static (SecretSlotState State, SensitiveVariableSlot Slot) Find(IEnumerable<SensitiveVariableSlot> slots, int variableSetId, VariableIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var matches = (slots ?? []).Where(s => s.VariableSetId == variableSetId && identity.Matches(s.Identity)).ToList();

        if (matches.Count == 0) return (SecretSlotState.NotFound, null);
        if (matches.Count > 1) return (SecretSlotState.Ambiguous, null);

        return (matches[0].HasValue ? SecretSlotState.AlreadySet : SecretSlotState.Empty, matches[0]);
    }
}
