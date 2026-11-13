using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Web;

namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// A real-socket Octopus REST API on 127.0.0.1 (port from the OS): one space whose configuration (<see cref="Resources"/>:
/// a project group, an environment, a lifecycle, project Projects-1 with its channel, process and variables) matches the
/// minimal export the import tests build, projects with paged releases and deployments, tasks with details,
/// interruptions, events, artifacts and machines (all mutable between fetches, so a test can change Octopus and
/// re-import it). Every request is recorded; a request without an accepted <c>X-Octopus-ApiKey</c> gets 401, and
/// <c>users/me</c> answers the user the key belongs to. <see cref="HoldPath"/> parks requests to one path until
/// <see cref="ReleaseHeldRequests"/>, so a test can observe a chunk that is still running.
/// </summary>
public sealed class StubOctopusServer : IAsyncDisposable
{
    public const string InstallationId = "8a6f2b3c-0000-4000-8000-000000000001";
    public const string DefaultUserId = "Users-1";

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentDictionary<string, string> _keyUsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _keyRestrictions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _heldRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StubOctopusServer(HttpListener listener, int port, string apiKey)
    {
        _listener = listener;
        _keyUsers[apiKey] = DefaultUserId;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _loop = Task.Run(ListenLoopAsync);
    }

    public Uri BaseUri { get; }

    public ConcurrentQueue<(string PathAndQuery, string ApiKey)> Requests { get; } = new();

    public Dictionary<string, List<Dictionary<string, object>>> Releases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<Dictionary<string, object>>> Deployments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, object>> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Interruptions per task id. A task without an entry has none.</summary>
    public Dictionary<string, List<Dictionary<string, object>>> Interruptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Events per deployment id. A deployment without an entry gets one default <c>DeploymentSucceeded</c> event.</summary>
    public Dictionary<string, List<Dictionary<string, object>>> Events { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Artifacts per task id. A task without an entry has none.</summary>
    public Dictionary<string, List<Dictionary<string, object>>> Artifacts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Dictionary<string, object>> Machines { get; } = [new() { ["Id"] = "Machines-1", ["Name"] = "web-01" }];

    /// <summary>
    /// Configuration by path below the space (<c>teams</c> below <c>api/</c>): a list is served as pages, anything else as it
    /// is; a path not listed is 404. The defaults are the configuration of the tests' minimal export, field for field.
    /// </summary>
    public Dictionary<string, object> Resources { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["projectgroups"] = new List<Dictionary<string, object>> { new() { ["Id"] = "ProjectGroups-1", ["Name"] = "Server group", ["Slug"] = "server-group" } },
        ["environments"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Environments-1", ["Name"] = "Server TEST", ["Slug"] = "server-test" } },
        ["lifecycles"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Lifecycles-1", ["Name"] = "Server lifecycle", ["Slug"] = "server-lifecycle", ["Phases"] = new[] { new Dictionary<string, object> { ["Id"] = "Phases-1", ["Name"] = "TEST", ["AutomaticDeploymentTargets"] = new[] { "Environments-1" } } } } },
        ["projects"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Projects-1", ["Name"] = "Server project", ["Slug"] = "server-project", ["ProjectGroupId"] = "ProjectGroups-1", ["LifecycleId"] = "Lifecycles-1", ["VariableSetId"] = "variableset-Projects-1", ["DeploymentProcessId"] = "deploymentprocess-Projects-1" } },
        ["projects/Projects-1/channels"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Channels-1", ["Name"] = "Default", ["Slug"] = "default", ["ProjectId"] = "Projects-1", ["IsDefault"] = true } },
        ["projects/Projects-1/deploymentprocesses"] = new Dictionary<string, object> { ["Id"] = "deploymentprocess-Projects-1", ["ProjectId"] = "Projects-1", ["Steps"] = Array.Empty<object>() },
        ["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = Array.Empty<object>() }
    };

    /// <summary>Requests whose path ends with this are held until <see cref="ReleaseHeldRequests"/>.</summary>
    public string HoldPath { get; set; }

    private readonly Dictionary<string, (int Times, HttpStatusCode Status)> _failures = new(StringComparer.Ordinal);

    /// <summary>Completes when a request to <see cref="HoldPath"/> arrives.</summary>
    public Task HeldPathRequested => _heldRequested.Task;

    public static StubOctopusServer Start(string apiKey)
    {
        var port = FreePort();
        var listener = new HttpListener();

        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        return new StubOctopusServer(listener, port, apiKey);
    }

    public StubOctopusServer Release(string projectId, string releaseId, string version)
    {
        Releases.TryAdd(projectId, []);
        Releases[projectId].Add(new Dictionary<string, object> { ["Id"] = releaseId, ["ProjectId"] = projectId, ["ChannelId"] = "Channels-1", ["Version"] = version, ["ReleaseNotes"] = $"notes {version}", ["SelectedPackages"] = Array.Empty<object>() });

        return this;
    }

    public StubOctopusServer Deployment(string projectId, string deploymentId, string releaseId, string taskId, string[] machineIds = null)
    {
        Deployments.TryAdd(projectId, []);
        Deployments[projectId].Add(new Dictionary<string, object> { ["Id"] = deploymentId, ["ProjectId"] = projectId, ["ChannelId"] = "Channels-1", ["EnvironmentId"] = "Environments-1", ["ReleaseId"] = releaseId, ["TaskId"] = taskId, ["Name"] = $"Deploy {deploymentId}", ["DeployedToMachineIds"] = machineIds ?? ["Machines-1"], ["Created"] = "2024-01-02T03:04:05Z" });
        Tasks[taskId] = new Dictionary<string, object> { ["Id"] = taskId, ["Name"] = "Deploy", ["Description"] = $"Deploy {deploymentId}", ["State"] = "Success", ["QueueTime"] = "2024-01-02T03:04:05Z", ["StartTime"] = "2024-01-02T03:04:06Z", ["CompletedTime"] = "2024-01-02T03:05:06Z", ["HasWarningsOrErrors"] = false };

        return this;
    }

    public StubOctopusServer Interruption(string taskId, string interruptionId, string type, bool isPending, string result = null)
    {
        var form = result == null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["Result"] = result };

        Interruptions.TryAdd(taskId, []);
        Interruptions[taskId].Add(new Dictionary<string, object> { ["Id"] = interruptionId, ["Title"] = $"Approve {interruptionId}", ["Type"] = type, ["IsPending"] = isPending, ["TaskId"] = taskId, ["Created"] = "2024-01-02T03:04:30Z", ["Form"] = new Dictionary<string, object> { ["Values"] = form } });

        return this;
    }

    public StubOctopusServer Event(string deploymentId, string eventId, string category, string message, string occurred)
    {
        Events.TryAdd(deploymentId, []);
        Events[deploymentId].Add(new Dictionary<string, object> { ["Id"] = eventId, ["Category"] = category, ["Message"] = message, ["Occurred"] = occurred, ["Username"] = "levi.y", ["RelatedDocumentIds"] = new[] { deploymentId } });

        return this;
    }

    public StubOctopusServer Artifact(string taskId, string artifactId, string filename)
    {
        Artifacts.TryAdd(taskId, []);
        Artifacts[taskId].Add(new Dictionary<string, object> { ["Id"] = artifactId, ["Filename"] = filename, ["Source"] = "Step 1", ["ServerTaskId"] = taskId, ["Created"] = "2024-01-02T03:05:00Z" });

        return this;
    }

    public void ReleaseHeldRequests() => _releaseHeld.TrySetResult();

    /// <summary>Accepts another API key, belonging to <paramref name="userId"/>.</summary>
    public StubOctopusServer AcceptKey(string apiKey, string userId)
    {
        _keyUsers[apiKey] = userId;

        return this;
    }

    /// <summary>The key's <c>users/{id}/permissions</c> limits these permissions to project Projects-1; every other key reads the whole space.</summary>
    public StubOctopusServer RestrictKey(string apiKey, params string[] permissions)
    {
        _keyRestrictions[apiKey] = permissions;

        return this;
    }

    /// <summary>
    /// The next <paramref name="times"/> requests whose path ends with <paramref name="pathSuffix"/> get <paramref name="status"/>.
    /// The default 405 is a failure the client neither retries nor tolerates; 400/403/409/422 refuse one item, which the fetch skips.
    /// </summary>
    public StubOctopusServer FailNext(string pathSuffix, int times = 1, HttpStatusCode status = HttpStatusCode.MethodNotAllowed)
    {
        lock (_failures) _failures[pathSuffix] = (times, status);

        return this;
    }

    private HttpStatusCode? FailureFor(string path)
    {
        lock (_failures)
        {
            var suffix = _failures.Keys.FirstOrDefault(k => _failures[k].Times > 0 && path.EndsWith(k, StringComparison.Ordinal));

            if (suffix == null) return null;

            var (times, status) = _failures[suffix];

            _failures[suffix] = (times - 1, status);

            return status;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var apiKey = context.Request.Headers["X-Octopus-ApiKey"];
            Requests.Enqueue((context.Request.Url!.PathAndQuery, apiKey));

            if (apiKey == null || !_keyUsers.TryGetValue(apiKey, out var userId))
            {
                Write(context, HttpStatusCode.Unauthorized, new { ErrorMessage = "Invalid API key" });
                return;
            }

            var (status, body) = await RouteAsync(context.Request.Url.AbsolutePath.TrimEnd('/'), HttpUtility.ParseQueryString(context.Request.Url.Query), userId, apiKey).ConfigureAwait(false);
            Write(context, status, body);
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private async Task<(HttpStatusCode, object)> RouteAsync(string path, NameValueCollection query, string userId, string apiKey)
    {
        if (FailureFor(path) is { } failure) return (failure, new { });
        if (path == "/api/users/me") return (HttpStatusCode.OK, new { Id = userId, Username = "importer", IsService = true });
        if (path == $"/api/users/{userId}/permissions") return (HttpStatusCode.OK, Permissions(_keyRestrictions.GetValueOrDefault(apiKey) ?? []));
        if (path == "/api") return (HttpStatusCode.OK, new { Application = "Octopus Deploy", Version = "2023.4.1", InstallationId, Links = new Dictionary<string, string> { ["Spaces"] = "/api/spaces{/id}" } });
        if (path == "/api/spaces/all") return (HttpStatusCode.OK, new[] { new { Id = "Spaces-1", Name = "Default", IsDefault = true } });
        if (path == "/api/teams") return Serve("teams", query);
        if (path == "/api/tasks") return (HttpStatusCode.OK, Page(query["ids"]!.Split(',').Where(Tasks.ContainsKey).Select(id => Tasks[id]).ToList(), query));
        if (!path.StartsWith("/api/Spaces-1/", StringComparison.Ordinal)) return (HttpStatusCode.NotFound, new { });

        var segments = path["/api/Spaces-1/".Length..].Split('/');

        if (HoldPath != null && path.EndsWith(HoldPath, StringComparison.Ordinal)) await HoldAsync().ConfigureAwait(false);

        return segments switch
        {
            ["projects", var projectId, "releases"] => (HttpStatusCode.OK, Page(Releases.GetValueOrDefault(projectId) ?? [], query)),
            ["deployments"] => (HttpStatusCode.OK, Page(Deployments.GetValueOrDefault(query["projects"]!) ?? [], query)),
            ["tasks", var taskId, "details"] => (HttpStatusCode.OK, new { Task = Tasks[taskId], ActivityLogs = new[] { new { Id = "a1", Name = "Deploy", Status = "Success", Children = Array.Empty<object>(), LogElements = new[] { new { Category = "Info", MessageText = $"log of {taskId}" } } } } }),
            ["interruptions"] => (HttpStatusCode.OK, Page(Interruptions.GetValueOrDefault(query["regarding"]!) ?? [], query)),
            ["artifacts"] => (HttpStatusCode.OK, Page(Artifacts.GetValueOrDefault(query["regarding"]!) ?? [], query)),
            ["events"] => (HttpStatusCode.OK, Page(Events.GetValueOrDefault(query["regarding"]!) ?? DefaultEvents(query["regarding"]), query)),
            ["machines"] => (HttpStatusCode.OK, Page(Machines, query)),
            _ => Serve(string.Join('/', segments), query)
        };
    }

    private (HttpStatusCode, object) Serve(string path, NameValueCollection query)
    {
        if (!Resources.TryGetValue(path, out var resource)) return (HttpStatusCode.NotFound, new { });

        return resource is List<Dictionary<string, object>> items ? (HttpStatusCode.OK, Page(items.Where(i => query["ids"] == null || query["ids"].Split(',').Contains((string)i["Id"])).ToList(), query)) : (HttpStatusCode.OK, resource);
    }

    private static List<Dictionary<string, object>> DefaultEvents(string deploymentId)
        => [new() { ["Id"] = "Events-1", ["Category"] = "DeploymentSucceeded", ["Message"] = "Deployed", ["RelatedDocumentIds"] = new[] { deploymentId } }];

    private async Task HoldAsync()
    {
        _heldRequested.TrySetResult();

        await _releaseHeld.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    private static object Page<T>(List<T> items, NameValueCollection query)
    {
        var skip = int.Parse(query["skip"] ?? "0");
        var take = int.Parse(query["take"] ?? "30");

        return new { Items = items.Skip(skip).Take(take).ToList(), TotalResults = items.Count, ItemsPerPage = take };
    }

    private static void Write(HttpListenerContext context, HttpStatusCode status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // The shape of a 2026.1 users/{id}/permissions answer: every permission the import reads, granted in Spaces-1.
    private static object Permissions(string[] restricted)
    {
        var permissions = new[] { "ProjectView", "ProcessView", "VariableView", "LibraryVariableSetView", "EnvironmentView", "FeedView", "AccountView", "TeamView", "CertificateView", "ReleaseView", "DeploymentView", "TaskView", "LifecycleView", "ProjectGroupView" };

        object Grant(string permission) => new { SpaceId = "Spaces-1", RestrictedToProjectIds = new[] { restricted.Contains(permission) ? "Projects-1" : "projects-all" }, RestrictedToEnvironmentIds = new[] { "environments-all" }, RestrictedToTenantIds = new[] { "tenants-all" }, RestrictedToProjectGroupIds = new[] { "projectgroups-unrelated" } };

        return new { Id = "permissions", SpacePermissions = permissions.ToDictionary(p => p, p => new[] { Grant(p) }), SystemPermissions = new[] { "SpaceView" }, IsPermissionsComplete = true };
    }

    public async ValueTask DisposeAsync()
    {
        _releaseHeld.TrySetResult();
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
