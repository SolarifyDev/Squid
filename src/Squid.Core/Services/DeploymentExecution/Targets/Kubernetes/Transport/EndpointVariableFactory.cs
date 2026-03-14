using System.Text.Json;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public static class EndpointVariableFactory
{
    public static VariableDto Make(string name, string value, bool isSensitive = false) => new()
    {
        Name = name,
        Value = value,
        Description = string.Empty,
        Type = Message.Enums.VariableType.String,
        IsSensitive = isSensitive,
        LastModifiedDate = DateTimeOffset.UtcNow,
        LastModifiedBy = 0
    };

    public static T TryDeserialize<T>(string json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;

        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return null; }
    }
}
