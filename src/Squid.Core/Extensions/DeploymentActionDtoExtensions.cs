using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Extensions;

public static class DeploymentActionDtoExtensions
{
    public static string GetProperty(this DeploymentActionDto action, string propertyName)
        => action?.Properties?
            .FirstOrDefault(p => string.Equals(p.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.PropertyValue;
}
