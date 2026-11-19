namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The name of a row a Squid owner holds now (a step of a process, an action of a step, a phase of a lifecycle, a variable of a set);
/// never values. <see cref="Id"/> is the row's id. <see cref="Scopes"/> is set on variables only: their scope as canonical
/// <c>VariableIdentity</c> tokens, since variables of one set may share a name and differ by scope.
/// </summary>
public sealed record OctopusImportDestinationChild(string Kind, int OwnerId, string Name)
{
    public int Id { get; init; }

    public IReadOnlyList<string> Scopes { get; init; }
}
