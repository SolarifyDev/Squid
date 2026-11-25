namespace Squid.Message.Models.Publishing;

/// <summary>What a recipe renders from. The package's version is not here: a process does not depend on it, the release selects it.</summary>
public class RecipeRenderContext
{
    public string PackageId { get; set; }

    public PackageFacts Facts { get; set; }

    public int HostPort { get; set; }

    public List<string> TargetRoles { get; set; } = new();
}
