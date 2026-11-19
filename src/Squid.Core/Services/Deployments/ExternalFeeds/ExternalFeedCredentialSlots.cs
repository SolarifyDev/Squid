using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

/// <summary>
/// Single source of truth for "can these feed credentials still be filled": only while no password is stored and the username is
/// empty or the one being filled in. A username someone set in Squid means the credentials are theirs. The data provider's
/// compare-and-set SQL repeats exactly this condition, so a change in between still never overwrites.
/// </summary>
public static class ExternalFeedCredentialSlots
{
    public static SecretSlotState StateOf(ExternalFeedCredentialSlot slot, string fillUsername)
    {
        if (slot == null) return SecretSlotState.NotFound;
        if (slot.HasPassword) return SecretSlotState.AlreadySet;

        var username = string.IsNullOrEmpty(fillUsername) ? null : fillUsername;

        return string.IsNullOrEmpty(slot.Username) || string.Equals(slot.Username, username, StringComparison.Ordinal) ? SecretSlotState.Empty : SecretSlotState.AlreadySet;
    }
}
