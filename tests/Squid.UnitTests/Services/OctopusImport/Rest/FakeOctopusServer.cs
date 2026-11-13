using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Web;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// A small in-memory Octopus REST API: one space, projects with paged releases and deployments, tasks with
/// details, interruptions, events and artifacts, plus any configuration resource put in <see cref="Resources"/>
/// (<see cref="WithFixtureSpace"/> loads the 2026.1 fixtures). Paths follow the real server (space-prefixed unless spaceless).
/// </summary>
internal sealed class FakeOctopusServer
{
    public const string Url = "https://octopus.example.com/";
    public const string ApiKey = "API-FAKE0000000000000000";
    public const string UserId = "Users-7";

    /// <summary>The master key the stub factory fingerprints keys with, as the production factory does with the configured one.</summary>
    public static readonly byte[] MasterKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    /// <summary>The Octopus user each API key belongs to; a key not listed is answered as <see cref="UserId"/>.</summary>
    public Dictionary<string, string> KeyUsers { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>users/{id}/permissions</c> answer per API key; a key not listed gets the unrestricted 2026.1 fixture.</summary>
    public Dictionary<string, JsonNode> KeyPermissions { get; } = new(StringComparer.Ordinal);

    public bool Spaceless { get; init; }

    public string InstallationId { get; init; } = "inst-1";

    public Dictionary<string, List<object>> Releases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<Dictionary<string, object>>> Deployments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, object>> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Configuration resources by path below the space (root resources such as <c>teams</c> by path below <c>api/</c>). An
    /// array, or a page's <c>Items</c>, is served paged and filtered by <c>ids</c>; any other object is served as it is.
    /// </summary>
    public Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serves every configuration fixture of <see cref="OctopusServer2026Fixtures"/> at the path a 2026.1 server serves it.</summary>
    public FakeOctopusServer WithFixtureSpace()
    {
        foreach (var name in OctopusServer2026Fixtures.Names)
            if (FixturePath(name) is { } path) Resources[path] = OctopusServer2026Fixtures.Node(name);

        foreach (var projectId in new[] { "Projects-1", "Projects-2" })
            Resources[$"projects/{projectId}/variables"] = OctopusServer2026Fixtures.Node($"variables-variableset-{projectId}");

        return this;
    }

    // "environments-all" → "environments"; "projects-Projects-1-channels" → "projects/Projects-1/channels"; history fixtures are not configuration.
    private static string FixturePath(string fixture)
    {
        if (fixture.EndsWith("-all", StringComparison.Ordinal) && fixture != "spaces-all") return fixture[..^"-all".Length];
        if (fixture.StartsWith("variables-", StringComparison.Ordinal)) return "variables/" + fixture["variables-".Length..];

        var parts = fixture.Split('-');

        return parts is ["projects", "Projects", var n, var child] && child != "releases" ? $"projects/Projects-{n}/{child}" : null;
    }

    /// <summary>Answers a request before the normal routes when it returns a response (a fault, a 404, a huge body).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Override { get; set; }

    public OctopusRestOptions ClientOptions { get; init; } = new();

    /// <summary>The feed the release process snapshot's package comes from.</summary>
    public string SnapshotFeedId { get; set; } = "Feeds-7";

    public OctopusRestStubHandler Handler { get; }

    public FakeOctopusServer() => Handler = new OctopusRestStubHandler(request => Override?.Invoke(request) ?? Respond(request));

    public IOctopusRestClientFactory Factory => new StubFactory(Handler, ClientOptions);

    public IEnumerable<string> Paths => Handler.PathsAndQueries;

    public FakeOctopusServer Deployment(string projectId, int number, string state = "Success")
    {
        var taskId = $"ServerTasks-{number}";

        Deployments.TryAdd(projectId, []);
        Deployments[projectId].Add(new Dictionary<string, object> { ["Id"] = $"Deployments-{number}", ["ProjectId"] = projectId, ["ChannelId"] = "Channels-1", ["EnvironmentId"] = "Environments-1", ["ReleaseId"] = "Releases-1", ["TaskId"] = taskId, ["Name"] = $"Deploy {number}", ["DeployedToMachineIds"] = new[] { "Machines-1", "Machines-9" }, ["Created"] = "2024-01-02T03:04:05Z" });
        Tasks[taskId] = new Dictionary<string, object> { ["Id"] = taskId, ["Name"] = "Deploy", ["Description"] = $"Deploy {number}", ["State"] = state, ["QueueTime"] = "2024-01-02T03:04:05Z", ["StartTime"] = "2024-01-02T03:04:06Z", ["CompletedTime"] = "2024-01-02T03:05:06Z", ["HasWarningsOrErrors"] = false };

        return this;
    }

    public FakeOctopusServer Release(string projectId, int number, string notes = "notes")
    {
        Releases.TryAdd(projectId, []);
        Releases[projectId].Add(new { Id = $"Releases-{number}", ProjectId = projectId, ChannelId = "Channels-1", Version = $"1.0.{number}", ReleaseNotes = notes, ProjectDeploymentProcessSnapshotId = "deploymentprocess-Projects-1-s-1", SelectedPackages = new[] { new { ActionName = "Deploy web", PackageReferenceName = "", Version = "1.0.0" } } });

        return this;
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        var prefix = Spaceless ? "/api" : "/api/Spaces-1";

        if (path == "/api") return OctopusRestStubHandler.Json(new { Application = "Octopus Deploy", Version = "2023.4.1", InstallationId, Links = Spaceless ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Spaces"] = "/api/spaces{/id}" } });
        if (path == "/api/users/me") return OctopusRestStubHandler.Json(new { Id = KeyUsers.GetValueOrDefault(KeyOf(request)) ?? UserId, Username = "importer", IsService = true });
        if (path.StartsWith("/api/users/", StringComparison.Ordinal) && path.EndsWith("/permissions", StringComparison.Ordinal)) return OctopusRestStubHandler.Json(KeyPermissions.GetValueOrDefault(KeyOf(request) ?? string.Empty) ?? OctopusServer2026Fixtures.Node("users-permissions-unrestricted"));
        if (path == "/api/spaces/all") return OctopusRestStubHandler.Json(new[] { new { Id = "Spaces-1", Name = "Default", IsDefault = true } });
        if (path == "/api/teams") return Serve("teams", query) ?? OctopusRestStubHandler.Status(HttpStatusCode.NotFound);
        if (path == "/api/tasks") return OctopusRestStubHandler.Json(OctopusRestStubHandler.Page(query["ids"]!.Split(',').Where(Tasks.ContainsKey).Select(id => Tasks[id]).ToList(), 0));
        if (!path.StartsWith(prefix + "/", StringComparison.Ordinal)) return OctopusRestStubHandler.Status(HttpStatusCode.NotFound);

        return RespondInSpace(path[prefix.Length..], query);
    }

    private HttpResponseMessage RespondInSpace(string path, System.Collections.Specialized.NameValueCollection query)
    {
        var segments = path.Trim('/').Split('/');

        return segments switch
        {
            ["projects", var projectId, "releases"] => Paged(Releases.GetValueOrDefault(projectId) ?? [], query),
            ["deployments"] => Paged(Deployments.GetValueOrDefault(query["projects"]!) ?? [], query),
            ["tasks", var taskId, "details"] => OctopusRestStubHandler.Json(new { Task = Tasks[taskId], ActivityLogs = new[] { new { Id = "a1", Name = "Deploy", Status = "Success", Children = Array.Empty<object>(), LogElements = new[] { new { Category = "Info", MessageText = $"log of {taskId}" } } } } }),
            ["interruptions"] => Paged(new List<object> { new { Id = "Interruptions-1", Title = "Approve", Type = "ManualIntervention", IsPending = false, TaskId = query["regarding"] } }, query),
            ["events"] => Paged(new List<object> { new { Id = "Events-1", Category = "DeploymentSucceeded", Message = "ok", RelatedDocumentIds = new[] { query["regarding"] } } }, query),
            ["artifacts"] => Paged(new List<object> { new { Id = "Artifacts-1", Filename = "report.txt", Source = "Octopus" } }, query),
            ["deploymentprocesses", _] => OctopusRestStubHandler.Json(new { Id = "deploymentprocess-Projects-1-s-1", Steps = new[] { new { Id = "s1", Name = "Deploy web", Actions = new[] { new { Id = "a1", Name = "Deploy web", Packages = new[] { new { Id = "p1", Name = "", PackageId = "web", FeedId = SnapshotFeedId } } } } } } }),
            _ => Serve(path.Trim('/'), query) ?? OctopusRestStubHandler.Status(HttpStatusCode.NotFound)
        };
    }

    private HttpResponseMessage Serve(string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (!Resources.TryGetValue(path, out var node)) return null;

        var items = node switch
        {
            JsonArray array => array,
            JsonObject page when page["Items"] is JsonArray pageItems => pageItems,
            _ => null
        };

        if (items == null) return OctopusRestStubHandler.Json(node);

        return Paged(items.OfType<JsonObject>().Where(i => Requested(i, query)).ToList(), query);
    }

    // ids= picks those ids; includeSystem=false leaves out Octopus's system teams ("teams-…"), as the real server does.
    private static bool Requested(JsonObject item, System.Collections.Specialized.NameValueCollection query)
    {
        var id = item["Id"]?.GetValue<string>() ?? string.Empty;

        if (query["ids"] is { } ids && !ids.Split(',').Contains(id, StringComparer.OrdinalIgnoreCase)) return false;

        return query["includeSystem"] != "false" || !id.StartsWith("teams-", StringComparison.Ordinal);
    }

    private static string KeyOf(HttpRequestMessage request) => request.Headers.TryGetValues(OctopusRestClient.ApiKeyHeader, out var values) ? values.Single() : null;

    private static HttpResponseMessage Paged<T>(List<T> items, System.Collections.Specialized.NameValueCollection query)
    {
        var skip = int.Parse(query["skip"] ?? "0");
        var take = int.Parse(query["take"] ?? "30");

        return OctopusRestStubHandler.Json(OctopusRestStubHandler.Page(items.Skip(skip).Take(take).ToList(), items.Count));
    }

    private sealed class StubFactory(OctopusRestStubHandler handler, OctopusRestOptions options) : IOctopusRestClientFactory
    {
        public OctopusRestClient Create(string serverUrl, string apiKey) => new(serverUrl, apiKey, options, new NonDisposingHandler(handler), (_, _) => Task.CompletedTask) { KeyFingerprint = OctopusRestKeyBinding.Fingerprint(MasterKey, apiKey) };
    }

    // Each chunk disposes its client; the recorded requests must survive across chunks.
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
