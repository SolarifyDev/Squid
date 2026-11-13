using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>A REST resource rewritten into the shape of the matching Octopus export document.</summary>
/// <param name="WithheldSecrets">Paths (from the document root) of every secret the server withheld, e.g. <c>Password</c>, <c>Variables[1].Value</c>, <c>Steps[0].Actions[0].Properties['Octopus.Action.X']</c>.</param>
public sealed record OctopusRestNormalizedDocument(OctopusResourceKind Kind, string Id, JsonObject Document, IReadOnlyList<string> WithheldSecrets);

/// <summary>
/// The single seam between Octopus REST JSON and the export-document pipeline. Works on raw JSON nodes before any DTO
/// binding and never mutates its input:
/// <list type="bullet">
/// <item>drops members the server adds for its own bookkeeping (<c>Links</c>, <c>LastModified*</c>, <c>DataVersion</c>), except inside
/// <c>Properties</c> bags, whose keys are user data;</item>
/// <item>drops every incoming <c>Squid.Import.*</c> member at any depth, so a server cannot plant Squid's own markers;</item>
/// <item>reshapes per kind where REST and export differ (variable set <c>OwnerType</c>, process <c>OwnerId</c>, project
/// <c>DeploymentSettingsId</c>, deployment settings id and connectivity policy, team security groups, account credentials);</item>
/// <item>replaces every <c>SensitiveValue</c> object (<c>{HasValue, NewValue, Hint}</c>) and every sensitive variable value with
/// <c>null</c>. When the server had a value, the member name is listed in <see cref="WithheldSecretsProperty"/> on the object that
/// owns it — for a <c>Properties</c> bag, on the bag's owner (the step or action).</item>
/// </list>
/// Built-in feeds (<c>BuiltIn</c>, <c>OctopusProject</c>) are not importable and normalise to <c>null</c>. Ids stay verbatim.
/// </summary>
public static partial class OctopusRestDocumentNormalizer
{
    public const string ReservedPropertyPrefix = "Squid.Import.";
    public const string WithheldSecretsProperty = "Squid.Import.WithheldSecrets";
    public const string PropertyBagName = "Properties";
    public const string LibraryVariableSetOwnerType = "LibraryVariableSet";
    public const string ProjectOwnerType = "Project";
    public const string DeploymentSettingsIdPrefix = "deploymentsettings-";

    public static IReadOnlyList<string> ServerOnlyMembers { get; } = ["Links", "LastModifiedOn", "LastModifiedBy", "LastModified", "DataVersion"];

    public static IReadOnlyList<string> BuiltInFeedTypes => OctopusBuiltInFeeds.FeedTypes;

    private static readonly HashSet<string> SensitiveValueMembers = new(["HasValue", "NewValue", "Hint"], StringComparer.Ordinal);

    /// <param name="ownerId">The project the resource was fetched under; fills a missing owner (deployment settings, variable set, process).</param>
    public static OctopusRestNormalizedDocument Normalize(OctopusResourceKind kind, JsonObject resource, string ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (IsExcluded(kind, resource)) return null;

        var document = resource.DeepClone().AsObject();

        StripMembers(document, isPropertyBag: false);
        ShapeAsExport(kind, document, ownerId);

        var withheld = WithholdSecrets(kind, document);

        return new OctopusRestNormalizedDocument(kind, ReadString(document, "Id"), document, withheld);
    }

    public static bool IsExcluded(OctopusResourceKind kind, JsonObject resource)
        => kind == OctopusResourceKind.Feed && OctopusBuiltInFeeds.IsBuiltInType(ReadString(resource, "FeedType"));

    // ── Strip ───────────────────────────────────────────────────────────────

    private static void StripMembers(JsonObject node, bool isPropertyBag)
    {
        foreach (var name in node.Select(m => m.Key).Where(name => IsReserved(name) || (!isPropertyBag && ServerOnlyMembers.Contains(name))).ToList())
            node.Remove(name);

        foreach (var (name, value) in node)
            StripChild(value, name == PropertyBagName);
    }

    private static void StripChild(JsonNode value, bool isPropertyBag)
    {
        if (value is JsonObject child) StripMembers(child, isPropertyBag);

        if (value is not JsonArray array) return;

        foreach (var item in array.OfType<JsonObject>())
            StripMembers(item, isPropertyBag: false);
    }

    private static bool IsReserved(string name) => name.StartsWith(ReservedPropertyPrefix, StringComparison.OrdinalIgnoreCase);

    // ── Per-kind shape ──────────────────────────────────────────────────────

    private static void ShapeAsExport(OctopusResourceKind kind, JsonObject document, string ownerId)
    {
        switch (kind)
        {
            case OctopusResourceKind.VariableSet or OctopusResourceKind.VariableSetSnapshot:
                AddVariableSetOwner(document, ownerId);
                break;
            case OctopusResourceKind.DeploymentProcess or OctopusResourceKind.DeploymentProcessSnapshot:
                MoveProjectIdToOwnerId(document, ownerId);
                break;
            case OctopusResourceKind.Project:
                AddDeploymentSettingsId(document);
                break;
            case OctopusResourceKind.DeploymentSettings:
                ShapeDeploymentSettings(document, ownerId);
                break;
            case OctopusResourceKind.Team:
                FlattenExternalSecurityGroups(document);
                break;
            case OctopusResourceKind.Account:
                FoldSecretsIntoCredentials(document);
                break;
        }
    }

    // REST variable sets carry no OwnerType; Squid decides library vs project ownership from it.
    private static void AddVariableSetOwner(JsonObject document, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ReadString(document, "OwnerId")) && !string.IsNullOrWhiteSpace(ownerId)) document["OwnerId"] = ownerId;

        if (!string.IsNullOrWhiteSpace(ReadString(document, "OwnerType"))) return;

        document["OwnerType"] = OwnerTypeOf(ReadString(document, "OwnerId"));
    }

    public static string OwnerTypeOf(string ownerId)
        => ownerId != null && ownerId.StartsWith("LibraryVariableSets-", StringComparison.OrdinalIgnoreCase) ? LibraryVariableSetOwnerType : ProjectOwnerType;

    // REST names the owning project ProjectId; the export (and the graph) use OwnerId.
    private static void MoveProjectIdToOwnerId(JsonObject document, string ownerId)
    {
        var projectId = ReadString(document, "ProjectId");

        document.Remove("ProjectId");

        if (!string.IsNullOrWhiteSpace(ReadString(document, "OwnerId"))) return;

        document["OwnerId"] = projectId ?? ownerId;
    }

    private static void AddDeploymentSettingsId(JsonObject document)
    {
        if (!string.IsNullOrWhiteSpace(ReadString(document, "DeploymentSettingsId"))) return;

        document["DeploymentSettingsId"] = DeploymentSettingsIdPrefix + ReadString(document, "Id");
    }

    // Also shapes an older server's project document (settings embedded on the project) into a settings document.
    private static void ShapeDeploymentSettings(JsonObject document, string ownerId)
    {
        var projectId = ReadString(document, "ProjectId") ?? ownerId ?? throw new ArgumentException("Deployment settings need the id of the project they belong to.", nameof(ownerId));

        document["ProjectId"] = projectId;

        if (!(ReadString(document, "Id") ?? string.Empty).StartsWith(DeploymentSettingsIdPrefix, StringComparison.OrdinalIgnoreCase)) document["Id"] = DeploymentSettingsIdPrefix + projectId;

        if (document.ContainsKey("ConnectivityPolicy") || !document.ContainsKey("ProjectConnectivityPolicy")) return;

        document["ConnectivityPolicy"] = document["ProjectConnectivityPolicy"]!.DeepClone();
        document.Remove("ProjectConnectivityPolicy");
    }

    // REST lists security groups as {Id, DisplayName, DisplayIdAndName}; the export as ids.
    private static void FlattenExternalSecurityGroups(JsonObject document)
    {
        if (document["ExternalSecurityGroups"] is not JsonArray groups) return;

        var ids = groups.Select(SecurityGroupId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => (JsonNode)JsonValue.Create(id)).ToArray();

        document["ExternalSecurityGroups"] = new JsonArray(ids);
    }

    private static string SecurityGroupId(JsonNode group)
        => group is JsonObject reference ? ReadString(reference, "Id") : group is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;

    // REST flattens account secrets per account type; the export (and the credentials-omitted marker) expect a Credentials object.
    private static void FoldSecretsIntoCredentials(JsonObject document)
    {
        if (document.ContainsKey("Credentials")) return;

        var secrets = document.Where(m => m.Value is JsonObject value && IsSensitiveValue(value)).Select(m => m.Key).ToList();

        if (secrets.Count == 0) return;

        var credentials = new JsonObject();

        foreach (var name in secrets)
        {
            credentials[name] = document[name]!.DeepClone();
            document.Remove(name);
        }

        document["Credentials"] = credentials;
    }

    // ── Secrets ─────────────────────────────────────────────────────────────

    private static List<string> WithholdSecrets(OctopusResourceKind kind, JsonObject document)
    {
        var withheld = new List<string>();

        if (kind is OctopusResourceKind.VariableSet or OctopusResourceKind.VariableSetSnapshot) WithholdSensitiveVariableValues(document, withheld);

        WithholdSensitiveValues(document, string.Empty, document, withheld);

        return withheld;
    }

    // The API never returns a sensitive variable's value and cannot say whether one exists: always withheld.
    private static void WithholdSensitiveVariableValues(JsonObject document, List<string> withheld)
    {
        if (document["Variables"] is not JsonArray variables) return;

        for (var index = 0; index < variables.Count; index++)
        {
            if (variables[index] is not JsonObject variable || !IsSensitiveVariable(variable)) continue;

            variable["Value"] = null;
            withheld.Add($"Variables[{index}].Value");
            MarkWithheld(variable, "Value");
        }
    }

    private static bool IsSensitiveVariable(JsonObject variable)
        => variable["IsSensitive"] is JsonValue flag && flag.TryGetValue<bool>(out var isSensitive) && isSensitive || string.Equals(ReadString(variable, "Type"), "Sensitive", StringComparison.OrdinalIgnoreCase);

    private static void WithholdSensitiveValues(JsonObject node, string path, JsonObject markTarget, List<string> withheld)
    {
        foreach (var (name, value) in node.ToList())
        {
            var memberPath = MemberPath(path, name);

            if (value is JsonObject secret && IsSensitiveValue(secret))
                WithholdMember(node, name, memberPath, markTarget, withheld);
            else if (value is JsonObject child)
                WithholdSensitiveValues(child, memberPath, name == PropertyBagName ? node : child, withheld);
            else if (value is JsonArray array)
                WithholdSensitiveItems(array, memberPath, withheld);
        }
    }

    private static void WithholdSensitiveItems(JsonArray array, string path, List<string> withheld)
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item) continue;

            if (IsSensitiveValue(item))
                WithholdItem(array, index, $"{path}[{index}]", withheld);
            else
                WithholdSensitiveValues(item, $"{path}[{index}]", item, withheld);
        }
    }

    private static void WithholdMember(JsonObject owner, string name, string path, JsonObject markTarget, List<string> withheld)
    {
        var hadValue = HasValue((JsonObject)owner[name]);

        owner[name] = null;

        if (!hadValue) return;

        withheld.Add(path);
        MarkWithheld(markTarget, name);
    }

    private static void WithholdItem(JsonArray array, int index, string path, List<string> withheld)
    {
        var hadValue = HasValue((JsonObject)array[index]);

        array[index] = null;

        if (hadValue) withheld.Add(path);
    }

    private static void MarkWithheld(JsonObject target, string memberName)
    {
        if (target[WithheldSecretsProperty] is not JsonArray names)
        {
            names = [];
            target[WithheldSecretsProperty] = names;
        }

        if (!names.Any(n => n?.GetValue<string>() == memberName)) names.Add(memberName);
    }

    /// <summary>Octopus's write-only secret resource: <c>{HasValue, NewValue}</c>, plus <c>Hint</c> on newer servers.</summary>
    public static bool IsSensitiveValue(JsonObject value)
        => value["HasValue"] is JsonValue flag && flag.TryGetValue<bool>(out _) && value.All(m => SensitiveValueMembers.Contains(m.Key));

    private static bool HasValue(JsonObject secret) => secret["HasValue"]!.GetValue<bool>();

    private static string MemberPath(string path, string name)
    {
        var member = SimpleMemberName().IsMatch(name) ? name : $"['{name}']";

        return path.Length == 0 || member.StartsWith('[') ? path + member : $"{path}.{member}";
    }

    private static string ReadString(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleMemberName();
}
