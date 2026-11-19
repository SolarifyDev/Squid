namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// What inserting one selected child into an owner an earlier import created wrote: the new row's id, the rows written with it (the
/// actions of a step, keyed by Octopus source id) and the mapping diagnostics. With a blocker in <see cref="Diagnostics"/> nothing was written.
/// </summary>
public sealed record OctopusImportInsertedChild(int Id, IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics, IReadOnlyDictionary<string, int> Followers)
{
    public static OctopusImportInsertedChild Blocked(IReadOnlyList<OctopusImportDiagnosticDto> diagnostics) => new(0, diagnostics, new Dictionary<string, int>());
}
