namespace Squid.Core.Services.Deployments.Validation;

public sealed class DeploymentValidationIssue
{
    public required DeploymentValidationIssueCode Code { get; init; }

    public required string Message { get; init; }

    public bool IsBlocking { get; init; } = true;
}
