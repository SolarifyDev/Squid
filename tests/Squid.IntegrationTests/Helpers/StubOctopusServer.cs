using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Web;

namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// A real-socket Octopus REST API on 127.0.0.1 (port from the OS): one space, projects with paged releases and
/// deployments, tasks with details, interruptions, events, artifacts and machines (all mutable between fetches, so a
/// test can change history in "Octopus" and re-import it). Every request is recorded; a request
/// without the expected <c>X-Octopus-ApiKey</c> gets 401. <see cref="HoldReleases"/> parks release requests until
/// <see cref="ReleaseHeldRequests"/>, so a test can observe a chunk that is still running.
/// </summary>
public sealed class StubOctopusServer : IAsyncDisposable
{
    public const string InstallationId = "8a6f2b3c-0000-4000-8000-000000000001";

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _apiKey;
    private readonly TaskCompletionSource _releasesRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StubOctopusServer(HttpListener listener, int port, string apiKey)
    {
        _listener = listener;
        _apiKey = apiKey;
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

    public bool HoldReleases { get; set; }

    public Task ReleasesRequested => _releasesRequested.Task;

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

            if (apiKey != _apiKey)
            {
                Write(context, HttpStatusCode.Unauthorized, new { ErrorMessage = "Invalid API key" });
                return;
            }

            var (status, body) = await RouteAsync(context.Request.Url.AbsolutePath.TrimEnd('/'), HttpUtility.ParseQueryString(context.Request.Url.Query)).ConfigureAwait(false);
            Write(context, status, body);
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private async Task<(HttpStatusCode, object)> RouteAsync(string path, NameValueCollection query)
    {
        if (path == "/api") return (HttpStatusCode.OK, new { Application = "Octopus Deploy", Version = "2023.4.1", InstallationId, Links = new Dictionary<string, string> { ["Spaces"] = "/api/spaces{/id}" } });
        if (path == "/api/spaces/all") return (HttpStatusCode.OK, new[] { new { Id = "Spaces-1", Name = "Default", IsDefault = true } });
        if (path == "/api/tasks") return (HttpStatusCode.OK, Page(query["ids"]!.Split(',').Where(Tasks.ContainsKey).Select(id => Tasks[id]).ToList(), query));
        if (!path.StartsWith("/api/Spaces-1/", StringComparison.Ordinal)) return (HttpStatusCode.NotFound, new { });

        var segments = path["/api/Spaces-1/".Length..].Split('/');

        if (segments is ["projects", _, "releases"]) await HoldIfRequestedAsync().ConfigureAwait(false);

        return segments switch
        {
            ["projects", var projectId, "releases"] => (HttpStatusCode.OK, Page(Releases.GetValueOrDefault(projectId) ?? [], query)),
            ["deployments"] => (HttpStatusCode.OK, Page(Deployments.GetValueOrDefault(query["projects"]!) ?? [], query)),
            ["tasks", var taskId, "details"] => (HttpStatusCode.OK, new { Task = Tasks[taskId], ActivityLogs = new[] { new { Id = "a1", Name = "Deploy", Status = "Success", Children = Array.Empty<object>(), LogElements = new[] { new { Category = "Info", MessageText = $"log of {taskId}" } } } } }),
            ["interruptions"] => (HttpStatusCode.OK, Page(Interruptions.GetValueOrDefault(query["regarding"]!) ?? [], query)),
            ["artifacts"] => (HttpStatusCode.OK, Page(Artifacts.GetValueOrDefault(query["regarding"]!) ?? [], query)),
            ["events"] => (HttpStatusCode.OK, Page(Events.GetValueOrDefault(query["regarding"]!) ?? DefaultEvents(query["regarding"]), query)),
            ["machines", "all"] => (HttpStatusCode.OK, Machines),
            _ => (HttpStatusCode.NotFound, new { })
        };
    }

    private static List<Dictionary<string, object>> DefaultEvents(string deploymentId)
        => [new() { ["Id"] = "Events-1", ["Category"] = "DeploymentSucceeded", ["Message"] = "Deployed", ["RelatedDocumentIds"] = new[] { deploymentId } }];

    private async Task HoldIfRequestedAsync()
    {
        _releasesRequested.TrySetResult();

        if (HoldReleases) await _releaseHeld.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
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
