namespace Squid.Core.Services.Deployments.Validation;

public sealed class DeploymentValidationReport
{
    public List<DeploymentValidationIssue> Issues { get; } = new();

    public bool IsValid => Issues.All(i => !i.IsBlocking);

    public string Message => IsValid
        ? "Deployment validation passed."
        : string.Join("; ", Issues.Where(i => i.IsBlocking).Select(i => i.Message));

    public void AddBlockingIssue(DeploymentValidationIssueCode code, string message)
    {
        Issues.Add(new DeploymentValidationIssue
        {
            Code = code,
            Message = message,
            IsBlocking = true
        });
    }
}
