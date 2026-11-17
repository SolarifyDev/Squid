namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// What the bare child ids (the ids recorded before child ids were owner-qualified) tell the re-import policy, as normalised
/// (source type, bare id) keys: <see cref="Shared"/> are the ones more than one child of the source shares (a project and its clone);
/// <see cref="Recorded"/> are the bare ids of the live owner-qualified source-map rows, whichever owner they are recorded under.
/// </summary>
public sealed record OctopusImportLegacyChildIds(IReadOnlySet<(string Type, string Id)> Shared, IReadOnlySet<(string Type, string Id)> Recorded)
{
    public static OctopusImportLegacyChildIds None { get; } = new(new HashSet<(string Type, string Id)>(), new HashSet<(string Type, string Id)>());
}
