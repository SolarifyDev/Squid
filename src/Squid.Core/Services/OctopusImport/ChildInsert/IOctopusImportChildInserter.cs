using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.ChildInsert;

/// <summary>
/// Inserts one kind of selected child (a step, an action, a lifecycle phase, a variable) into the Squid owner an earlier import created,
/// writing only the new rows: the owner and its other children are never rewritten (at most their order moves up by one to make room).
/// </summary>
public interface IOctopusImportChildInserter : IScopedDependency
{
    OctopusResourceKind Kind { get; }

    /// <summary>The owner's rows now, as destination id → order among its siblings (empty for a kind that is always appended).</summary>
    Task<IReadOnlyDictionary<int, int>> SiblingOrdersAsync(int ownerId, CancellationToken ct);

    /// <summary>Maps the child and, unless the mapping blocks, writes it. A blocked result wrote nothing.</summary>
    Task<OctopusImportInsertedChild> InsertAsync(OctopusImportChildInsert insert, CancellationToken ct);
}
