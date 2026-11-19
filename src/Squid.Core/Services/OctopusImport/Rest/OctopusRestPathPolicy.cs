using System.Text.RegularExpressions;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// The only GET requests the Octopus source may send. A GET is not automatically harmless on Octopus (an API key
/// listing, a certificate export, a machine discovery that dials a host), so every request path must match one of
/// <see cref="Templates"/> after <c>api/</c> and an optional <c>Spaces-N</c> segment. A <c>{id}</c> segment and every
/// query value must be an id (<see cref="IdPattern"/>, not <c>.</c> or <c>..</c>); callers escape ids, so an id
/// carrying <c>/</c>, <c>?</c> or <c>%</c> arrives escaped and is refused. Anything else is
/// <see cref="OctopusRestErrorKind.Denied"/>.
/// </summary>
public static class OctopusRestPathPolicy
{
    public const string IdPattern = "^[A-Za-z0-9._-]+$";

    private const string IdSegment = "{id}";

    private static readonly Regex Id = new(IdPattern, RegexOptions.Compiled);
    private static readonly Regex SpaceId = new("^Spaces-[0-9]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QueryKey = new("^[A-Za-z]+$", RegexOptions.Compiled);

    /// <summary>Space-scoped collections read whole: <c>{kind}</c> (paged, or filtered by <c>?ids=</c>) and <c>{kind}/all</c>. Single items are never addressed by path.</summary>
    public static readonly IReadOnlyList<string> Collections =
    [
        "projectgroups", "environments", "lifecycles", "feeds", "accounts", "teams", "libraryvariablesets", "actiontemplates",
        "machines", "workers", "workerpools", "certificates", "tenants", "projects", "channels"
    ];

    /// <summary>Per-project children read through <c>projects/{id}/{child}</c>.</summary>
    public static readonly IReadOnlyList<string> ProjectChildren = ["channels", "releases", "runbooks", "triggers", "deploymentsettings", "deploymentprocesses", "variables"];

    public static readonly IReadOnlyList<string> Templates =
    [
        "",
        "users/me",
        "users/{id}/permissions",
        "spaces/all",
        "tasks",
        "tasks/{id}/details",
        "variables/{id}",
        "deploymentprocesses/{id}",
        "deployments",
        "interruptions",
        "events",
        "artifacts",
        .. Collections,
        .. Collections.Select(c => c + "/all"),
        .. ProjectChildren.Select(c => "projects/{id}/" + c)
    ];

    private static readonly string[][] TemplateSegments = Templates.Select(t => t.Length == 0 ? [] : t.Split('/')).ToArray();

    public static void EnsureAllowed(string relativePath)
    {
        if (!IsAllowed(relativePath))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);
    }

    public static bool IsAllowed(string relativePath)
    {
        var (path, query) = Split(relativePath);

        return IsAllowedPath(path) && IsAllowedQuery(query);
    }

    /// <summary>The key's own identity: a 401/403 here means the key itself is refused, anywhere else only that item.</summary>
    public static bool IsIdentityPath(string relativePath)
    {
        var path = Split(relativePath).Path;

        return string.Equals(path, "api", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "api/users/me", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path part without the leading <c>/</c>, as the policy compares it.</summary>
    public static string PathOf(string relativePath) => Split(relativePath).Path;

    public static bool IsValidId(string value) => value != null && Id.IsMatch(value) && value != "." && value != "..";

    private static (string Path, string Query) Split(string relativePath)
    {
        var trimmed = (relativePath ?? string.Empty).TrimStart('/');
        var queryStart = trimmed.IndexOf('?');

        return queryStart < 0 ? (trimmed, string.Empty) : (trimmed[..queryStart], trimmed[(queryStart + 1)..]);
    }

    private static bool IsAllowedPath(string path)
    {
        var segments = path.Split('/');

        if (!string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)) return false;

        var rest = segments.Length > 1 && SpaceId.IsMatch(segments[1]) ? segments[2..] : segments[1..];

        return TemplateSegments.Any(template => Matches(template, rest));
    }

    private static bool Matches(string[] template, string[] segments)
    {
        if (template.Length != segments.Length) return false;

        for (var i = 0; i < template.Length; i++)
        {
            var matches = template[i] == IdSegment ? IsValidId(segments[i]) : string.Equals(template[i], segments[i], StringComparison.OrdinalIgnoreCase);

            if (!matches) return false;
        }

        return true;
    }

    // Every pair is key=value with an alphabetic key and a comma list of ids as the value.
    private static bool IsAllowedQuery(string query)
    {
        if (query.Length == 0) return true;

        return query.Split('&').All(IsAllowedQueryPair);
    }

    private static bool IsAllowedQueryPair(string pair)
    {
        var separator = pair.IndexOf('=');

        if (separator <= 0) return false;

        return QueryKey.IsMatch(pair[..separator]) && pair[(separator + 1)..].Split(',').All(IsValidId);
    }
}
