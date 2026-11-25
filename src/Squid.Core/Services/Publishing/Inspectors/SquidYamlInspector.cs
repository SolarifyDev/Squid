using Squid.Message.Models.Publishing;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Squid.Core.Services.Publishing.Inspectors;

/// <summary>
/// The author's own say: <c>recipe</c>, <c>run.port</c>, <c>run.command</c> and <c>health.path</c> from <c>squid.yaml</c>, recorded as given. Anything missing is simply
/// absent; a file that is not valid YAML (or not a mapping) is recorded in <see cref="PackageFactKeys.SquidError"/>, never thrown.
/// </summary>
public sealed class SquidYamlInspector : IPackageInspector
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public async Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
    {
        var text = await context.TryReadTextAsync("squid.yaml", ct).ConfigureAwait(false);

        if (text != null) Record(context, text);
    }

    private static void Record(PackageInspectionContext context, string text)
    {
        try
        {
            var root = Yaml.Deserialize<Dictionary<object, object>>(text);

            if (root == null) return;

            SetIfPresent(context, PackageFactKeys.SquidRecipe, Scalar(root, "recipe"));
            SetIfPresent(context, PackageFactKeys.SquidRunPort, Scalar(Child(root, "run"), "port"));
            SetIfPresent(context, PackageFactKeys.SquidRunCommand, Scalar(Child(root, "run"), "command"));
            SetIfPresent(context, PackageFactKeys.SquidHealthPath, Scalar(Child(root, "health"), "path"));
        }
        catch (Exception ex) when (ex is YamlException or InvalidCastException)
        {
            context.Set(PackageFactKeys.SquidError, ex.Message);
        }
    }

    private static Dictionary<object, object> Child(Dictionary<object, object> parent, string key) => parent != null && parent.TryGetValue(key, out var value) ? value as Dictionary<object, object> : null;

    private static string Scalar(Dictionary<object, object> parent, string key) => parent != null && parent.TryGetValue(key, out var value) && value is string text ? text : null;

    private static void SetIfPresent(PackageInspectionContext context, string key, string value)
    {
        if (value != null) context.Set(key, value);
    }
}
