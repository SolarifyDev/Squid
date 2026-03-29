using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

internal static class PackageVersionResolver
{
    internal static string Resolve(ActionExecutionContext ctx, string packageReferenceName = null)
    {
        if (ctx.SelectedPackages != null)
        {
            var match = packageReferenceName == null
                ? ctx.SelectedPackages.FirstOrDefault(sp =>
                    string.Equals(sp.ActionName, ctx.Action.Name, StringComparison.OrdinalIgnoreCase))
                : ctx.SelectedPackages.FirstOrDefault(sp =>
                    string.Equals(sp.ActionName, ctx.Action.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sp.PackageReferenceName, packageReferenceName, StringComparison.OrdinalIgnoreCase));

            if (match != null && !string.IsNullOrWhiteSpace(match.Version))
                return match.Version;
        }

        var versionVar = ctx.Variables?.FirstOrDefault(v =>
            string.Equals(v.Name, SpecialVariables.Action.PackageVersion, StringComparison.OrdinalIgnoreCase));

        return versionVar?.Value ?? string.Empty;
    }
}
