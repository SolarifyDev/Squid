namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class ResourceGenerationException : InvalidOperationException
{
    public List<string> Errors { get; }

    public ResourceGenerationException(List<string> errors)
        : base(string.Join("; ", errors))
    {
        Errors = errors;
    }
}
