using System.Text;
using System.Text.RegularExpressions;

namespace Squid.Core.Services.Publishing.Recipes;

/// <summary>
/// A recipe's bash script: its own template followed by the shared container template, with every <c>{{Name}}</c> replaced by its value in one pass. One
/// pass, so a value that itself contains <c>{{Other}}</c> is never substituted again; a name with no value is an error, so a typo in a template cannot ship.
/// </summary>
public static class RecipeScript
{
    private const string ResourcePrefix = "Squid.Core.Resources.Recipes.";
    private const string ContainerAppTemplate = "ContainerApp";

    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string Render(string recipeTemplate, IReadOnlyDictionary<string, string> values)
    {
        var template = Load(recipeTemplate) + Load(ContainerAppTemplate);

        return Placeholder.Replace(template, match => ValueOf(match.Groups[1].Value, values));
    }

    private static string ValueOf(string name, IReadOnlyDictionary<string, string> values)
    {
        return values.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"The script template uses '{{{{{name}}}}}' but no value was given for it.");
    }

    private static string Load(string template)
    {
        var assembly = typeof(RecipeScript).Assembly;
        var name = $"{ResourcePrefix}{template}.sh";

        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource '{name}' not found in assembly '{assembly.GetName().Name}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
