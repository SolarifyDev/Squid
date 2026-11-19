using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Message.Models.OctopusImport;

/// <summary>The current Squid state (never the values) of every secret a live <c>Missing</c> source-map row points at, read once per preview.</summary>
public sealed record OctopusImportSecretSlots(IReadOnlyList<SensitiveVariableSlot> Variables, IReadOnlyList<ExternalFeedCredentialSlot> Feeds)
{
    public static OctopusImportSecretSlots Empty { get; } = new([], []);
}
