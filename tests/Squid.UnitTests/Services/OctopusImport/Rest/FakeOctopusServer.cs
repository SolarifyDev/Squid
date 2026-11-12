using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// A small in-memory Octopus REST API: one space, projects with paged releases and deployments, tasks with
/// details, interruptions, events and artifacts. Paths follow the real server (space-prefixed unless spaceless).
/// </summary>
internal sealed class FakeOctopusServer
{
    public const string Url = "https://octopus.example.com/";
    public const string ApiKey = "API-FAKE0000000000000000";

    public bool Spaceless { get; init; }

    public string InstallationId { get; init; } = "inst-1";

    public Dictionary<string, List<object>> Releases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<Dictionary<string, object>>> Deployments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, object>> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public OctopusRestStubHandler Handler { get; }

    public FakeOctopusServer() => Handler = new OctopusRestStubHandler(Respond);

    public IOctopusRestClientFactory Factory => new StubFactory(Handler);

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
        if (path == "/api/spaces/all") return OctopusRestStubHandler.Json(new[] { new { Id = "Spaces-1", Name = "Default", IsDefault = true } });
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
            ["deploymentprocesses", _] => OctopusRestStubHandler.Json(new { Id = "deploymentprocess-Projects-1-s-1", Steps = new[] { new { Id = "s1", Name = "Deploy web", Actions = new[] { new { Id = "a1", Name = "Deploy web", Packages = new[] { new { Id = "p1", Name = "", PackageId = "web", FeedId = "Feeds-7" } } } } } } }),
            ["machines", "all"] => OctopusRestStubHandler.Json(new[] { new { Id = "Machines-1", Name = "web-01" } }),
            _ => OctopusRestStubHandler.Status(HttpStatusCode.NotFound)
        };
    }

    private static HttpResponseMessage Paged<T>(List<T> items, System.Collections.Specialized.NameValueCollection query)
    {
        var skip = int.Parse(query["skip"] ?? "0");
        var take = int.Parse(query["take"] ?? "30");

        return OctopusRestStubHandler.Json(OctopusRestStubHandler.Page(items.Skip(skip).Take(take).ToList(), items.Count));
    }

    private sealed class StubFactory(OctopusRestStubHandler handler) : IOctopusRestClientFactory
    {
        public OctopusRestClient Create(string serverUrl, string apiKey) => new(serverUrl, apiKey, new OctopusRestOptions(), new NonDisposingHandler(handler), (_, _) => Task.CompletedTask);
    }

    // Each chunk disposes its client; the recorded requests must survive across chunks.
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
