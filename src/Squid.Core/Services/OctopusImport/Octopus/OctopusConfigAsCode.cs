using System.Text.Json.Nodes;

namespace Squid.Core.Services.OctopusImport.Octopus;

/// <summary>
/// Octopus Config-as-Code (version-controlled) projects, Octopus 2021.3+: the process and settings live in a git repository, not in the
/// Octopus database, so neither source carries them and Squid does not import such a project. A project is version-controlled when
/// <c>IsVersionControlled</c> is true or <c>PersistenceSettings.Type</c> is <c>VersionControlled</c>; an older server has neither member
/// and reads as database-backed. One rule for the raw REST document and the bound DTO.
/// </summary>
public static class OctopusConfigAsCode
{
    public const string VersionControlledPersistenceType = "VersionControlled";
    public const string IsVersionControlledMember = "IsVersionControlled";
    public const string PersistenceSettingsMember = "PersistenceSettings";
    public const string PersistenceTypeMember = "Type";

    /// <summary>Where Octopus 2021.1–2021.2 kept a version-controlled project's git url and credentials; never read, never staged.</summary>
    public const string LegacyVersionControlSettingsMember = "VersionControlSettings";

    public static bool IsVersionControlled(JsonObject project) => ReadBool(project, IsVersionControlledMember) || IsVersionControlledType(ReadString(project?[PersistenceSettingsMember] as JsonObject, PersistenceTypeMember));

    public static bool IsVersionControlled(OctopusProjectDto project) => project != null && (project.IsVersionControlled || IsVersionControlledType(project.PersistenceSettings?.Type));

    private static bool IsVersionControlledType(string type) => string.Equals(type?.Trim(), VersionControlledPersistenceType, StringComparison.OrdinalIgnoreCase);

    private static bool ReadBool(JsonObject node, string member) => node?[member] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string ReadString(JsonObject node, string member) => node?[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
