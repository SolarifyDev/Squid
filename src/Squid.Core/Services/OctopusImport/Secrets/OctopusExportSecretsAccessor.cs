namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Request-scoped holder for the export secrets of the session currently being planned or confirmed.
/// The import handlers populate it from the session before running the planning pipeline, so the
/// preview planner, validator and mappers can resolve encrypted values without the decrypted plaintext
/// ever being persisted in the graph, the preview plan or the session row.
/// </summary>
public interface IOctopusExportSecretsAccessor : IScopedDependency
{
    OctopusExportSecrets Secrets { get; set; }
}

public sealed class OctopusExportSecretsAccessor : IOctopusExportSecretsAccessor
{
    private OctopusExportSecrets _secrets = OctopusExportSecrets.None;

    public OctopusExportSecrets Secrets
    {
        get => _secrets;
        set => _secrets = value ?? OctopusExportSecrets.None;
    }
}
