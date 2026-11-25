using System.Linq;
using Squid.Message.Models.Publishing;

namespace Squid.UnitTests.Services.Publishing.Recipes;

internal static class RecipeFacts
{
    public static PackageFacts Of(params (string Key, string Value)[] values)
    {
        var facts = new PackageFacts();

        foreach (var (key, value) in values) facts.Values[key] = value;

        return facts;
    }

    public static (string, string) Static() => (PackageFactKeys.MarkerIndexHtml, PackageFactKeys.True);

    public static (string, string) StaticDir(string dir) => (PackageFactKeys.MarkerIndexHtmlDir, dir);

    public static (string, string) Dockerfile() => (PackageFactKeys.MarkerDockerfile, PackageFactKeys.True);

    public static (string, string) Root(string root) => (PackageFactKeys.ArchiveRoot, root);

    public static RecipeRenderContext Context(PackageFacts facts, string packageId = "Acme.Web", int hostPort = 8080, params string[] roles)
    {
        return new RecipeRenderContext { PackageId = packageId, Facts = facts, HostPort = hostPort, TargetRoles = roles.Length == 0 ? ["web"] : roles.ToList() };
    }

    public static string ScriptOf(DeploymentPlan plan) => plan.Steps[1].Actions[0].Properties["Squid.Action.Script.ScriptBody"];
}
