using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>A hash of everything in a plan that ends up in a process, independent of the order of properties and of roles. Two plans with the same fingerprint make the same process.</summary>
public static class DeploymentPlanFingerprint
{
    public static string Of(DeploymentPlan plan)
    {
        var canonical = plan.Steps.Select(s => new { s.Name, Roles = s.TargetRoles.OrderBy(r => r, StringComparer.Ordinal).ToList(), Actions = s.Actions.Select(Canonical).ToList() });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }

    private static object Canonical(PlanAction action)
    {
        return new { action.Name, action.ActionType, Package = action.Package?.PackageId, Properties = action.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value }).ToList() };
    }
}
