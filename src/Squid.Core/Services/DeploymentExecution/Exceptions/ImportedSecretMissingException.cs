using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Exceptions;

/// <summary>
/// The deployment would use sensitive values an Octopus API import recorded as missing (D16). Thrown before any step runs; the
/// message names every such variable, its owner and what to do — never a value.
/// </summary>
public sealed class ImportedSecretMissingException : DeploymentValidationException
{
    public ImportedSecretMissingException(int deploymentId, IReadOnlyList<MissingImportedSecret> secrets) : base(BuildMessage(deploymentId, secrets))
    {
        DeploymentId = deploymentId;
        Secrets = secrets;
    }

    public int DeploymentId { get; }

    public IReadOnlyList<MissingImportedSecret> Secrets { get; }

    public static string BuildMessage(int deploymentId, IReadOnlyList<MissingImportedSecret> secrets)
        => $"Deployment {deploymentId} cannot start: {secrets.Count} sensitive variable(s) imported from Octopus have no value in this deployment.{System.Environment.NewLine}{string.Join(System.Environment.NewLine, secrets.Select(Line))}";

    private static string Line(MissingImportedSecret secret) => $"- \"{secret.Name}\"{ScopeText(secret)} in {OwnerText(secret)}: {Advice(secret.Reason)}";

    private static string ScopeText(MissingImportedSecret secret) => string.IsNullOrWhiteSpace(secret.Scope) ? string.Empty : $" ({secret.Scope})";

    private static string OwnerText(MissingImportedSecret secret) => string.IsNullOrWhiteSpace(secret.OwnerName) ? $"variable set {secret.VariableSetId}" : secret.OwnerName;

    private static string Advice(MissingImportedSecretReason reason) => reason switch
    {
        MissingImportedSecretReason.SnapshotPredatesValue => "a value has been set since this release was created, but the release's variable snapshot still holds the empty value. Update the release's variables or create a new release.",
        _ => "secret not imported from Octopus (the Octopus API never returns secret values). Set it in Squid, or import the Octopus export file of the same space to fill it. If it is empty in Octopus too, importing the export file records that and lets the deployment run."
    };
}
