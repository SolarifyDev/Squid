using Squid.Core.Services.Security;

namespace Squid.Core.Services.Deployments.Variables;

/// <summary>
/// Called at the end of every Variables save of a set, inside the save's transaction. Lets a concern that tracks a set's secrets (an Octopus
/// import's missing secrets) settle what the save changed, without the Variables layer depending on it.
/// </summary>
public interface IVariableSetSavedHook : IScopedDependency
{
    Task OnVariableSetSavedAsync(int spaceId, int variableSetId, CancellationToken ct);
}

/// <summary>What <see cref="VariableService"/> needs for sensitive values: masking, encryption for a fill, and the hooks a save runs.</summary>
public sealed class VariableSecretCollaborators(SensitiveVariableHandler handler, IVariableEncryptionService encryption = null, IEnumerable<IVariableSetSavedHook> savedHooks = null) : IScopedDependency
{
    public SensitiveVariableHandler Handler { get; } = handler;

    public IVariableEncryptionService Encryption { get; } = encryption;

    public IReadOnlyList<IVariableSetSavedHook> SavedHooks { get; } = savedHooks?.ToList() ?? [];
}
