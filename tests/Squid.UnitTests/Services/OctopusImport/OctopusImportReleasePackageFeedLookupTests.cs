using System.Collections;
using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Release package feed resolution is indexed once per plan: validating thousands of releases must not scan the whole graph and
/// every preview row once per package (a 5000-release project times a whole-space plan took minutes).
/// </summary>
public class OctopusImportReleasePackageFeedLookupTests
{
    // The graph is read a fixed number of times however many releases are validated; per package it grew with the release count.
    [Fact]
    public void Validate_ManyReleasesWithoutPackageFeeds_ReadsTheGraphAFixedNumberOfTimes()
    {
        var few = GraphReadsToValidate(releaseCount: 3);
        var many = GraphReadsToValidate(releaseCount: 60);

        many.ShouldBe(few, $"graph read {few} times for 3 releases but {many} times for 60: feed resolution scans the graph per package");
    }

    [Theory]
    [InlineData("Feeds-1", true)]                // exact
    [InlineData("feeds-1", true)]                // case-insensitive
    [InlineData("Feeds-2", true)]                // unique export-suffixed feed
    [InlineData(" Feeds-2 ", true)]              // prefix trimmed
    [InlineData("Feeds-3", false)]               // two suffixed feeds: ambiguous
    [InlineData("Feeds-4", false)]               // a feed the plan skips resolves nowhere
    [InlineData("Feeds-5", true)]                // already imported (destination known)
    [InlineData("Feeds-6", false)]               // reused without a destination id
    [InlineData("Feeds-7", true)]                // in the source map only
    [InlineData("12", true)]                     // a Squid feed id
    [InlineData("Feeds", false)]                 // a prefix of several feeds
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PlannedFeedIndex_MatchesTheFeedRules(string sourceFeedId, bool expected)
    {
        var previews = Previews(
            Feed("Feeds-1", OctopusImportPreviewAction.Create),
            Feed("Feeds-2-0123456789ABCDEF", OctopusImportPreviewAction.Create),
            Feed("Feeds-3-AAAA", OctopusImportPreviewAction.Create),
            Feed("Feeds-3-BBBB", OctopusImportPreviewAction.Create),
            Feed("Feeds-4", OctopusImportPreviewAction.Skip),
            Feed("Feeds-5", OctopusImportPreviewAction.AlreadyImported, 55),
            Feed("Feeds-6", OctopusImportPreviewAction.AlreadyImported),
            new OctopusImportResourceResultDto { SourceId = "Projects-1", SourceType = "Project", PreviewAction = OctopusImportPreviewAction.Create });
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [("Feed", "Feeds-7")] = new(70, "Created", null, null) }, null);

        new OctopusImportPlannedFeedIndex(previews, prior).HasPlannedDestination(sourceFeedId).ShouldBe(expected);
    }

    [Fact]
    public void PlannedFeedIndex_ReadsThePreviewRowsOnce_HoweverManyPackagesAsk()
    {
        var previews = new CountingDictionary(Previews(Feed("Feeds-1", OctopusImportPreviewAction.Create)));
        var index = new OctopusImportPlannedFeedIndex(previews);

        for (var i = 0; i < 100; i++) index.HasPlannedDestination($"Feeds-{i}");

        previews.ValueReads.ShouldBe(1);
    }

    // The release's process snapshot wins; the project's current action is the fallback; another project's action never answers.
    [Theory]
    [InlineData("snapshot-1", "Projects-1", "Feeds-Snapshot")]
    [InlineData("snapshot-missing", "Projects-1", "Feeds-Current")]
    [InlineData("snapshot-missing", "Projects-2", null)]
    public void Lookup_SnapshotThenTheProjectsCurrentAction(string snapshotId, string projectId, string expected)
    {
        var snapshot = new OctopusDeploymentProcessDto { Id = "snapshot-1", Steps = [new OctopusDeploymentStepDto { Actions = [Action("Feeds-Snapshot")] }] };
        var graph = Graph(Node("SNAPSHOT-1", OctopusResourceKind.DeploymentProcessSnapshot, snapshot));
        var current = new[] { Node("Actions-1", OctopusResourceKind.DeploymentAction, Action("Feeds-Current")) };
        var release = new OctopusReleaseDto { Id = "Releases-1", ProjectId = projectId, ProjectDeploymentProcessSnapshotId = snapshotId };

        new OctopusImportReleasePackageFeedLookup(graph, current).GetSourceFeedId(release, Package()).ShouldBe(expected);
    }

    private static int GraphReadsToValidate(int releaseCount)
    {
        var feed = Node("Feeds-1", OctopusResourceKind.Feed, new object());
        var action = Node("Actions-1", OctopusResourceKind.DeploymentAction, Action("Feeds-1"));
        var releases = Enumerable.Range(1, releaseCount).Select(i => Node($"Releases-{i}", OctopusResourceKind.Release, new OctopusReleaseDto { Id = $"Releases-{i}", ProjectId = "Projects-1", ProjectDeploymentProcessSnapshotId = $"snapshot-{i}", SelectedPackages = [Package()] })).ToList();
        var resources = new List<OctopusResourceNode> { feed, action };
        resources.AddRange(releases);
        var counted = new CountingList(resources);
        var graph = new OctopusResourceGraph(counted, [], [], []);
        var preview = new OctopusImportPreviewPlanDto { GeneratedAt = DateTimeOffset.UtcNow, Resources = resources.Select(r => new OctopusImportResourceResultDto { SourceId = r.SourceId, SourceType = r.Kind.ToString(), SourceName = r.Name, PreviewAction = OctopusImportPreviewAction.Create, OutcomeState = OctopusImportResourceOutcomeState.Pending }).ToList() };

        var result = new OctopusImportPreviewValidator().Validate(graph, new OctopusImportDependencyPlan(resources, [], [], []), new OctopusImportConflictDiscoveryResult([]), preview);

        result.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping);

        return counted.Reads;
    }

    private static OctopusResourceGraph Graph(params OctopusResourceNode[] resources) => new(resources, [], [], []);

    private static OctopusResourceNode Node(string sourceId, OctopusResourceKind kind, object source)
        => new(sourceId, sourceId, kind, OctopusDocumentKind.Project, $"{sourceId}.json", "Projects-1", null, false, source);

    private static OctopusDeploymentActionDto Action(string feedId) => new() { Id = "Actions-1", Name = "Deploy", Packages = [new OctopusActionPackageDto { Name = "app", FeedId = feedId }] };

    private static OctopusSelectedPackageDto Package() => new() { ActionName = "Deploy", PackageReferenceName = "app", Version = "1.2.3" };

    private static OctopusImportResourceResultDto Feed(string sourceId, OctopusImportPreviewAction action, int? destinationId = null)
        => new() { SourceId = sourceId, SourceType = "Feed", PreviewAction = action, DestinationId = destinationId };

    private static Dictionary<string, OctopusImportResourceResultDto> Previews(params OctopusImportResourceResultDto[] rows) => rows.ToDictionary(r => r.SourceId, StringComparer.OrdinalIgnoreCase);

    private sealed class CountingList(List<OctopusResourceNode> inner) : IReadOnlyList<OctopusResourceNode>
    {
        public int Reads { get; private set; }

        public OctopusResourceNode this[int index] => inner[index];

        public int Count => inner.Count;

        public IEnumerator<OctopusResourceNode> GetEnumerator()
        {
            Reads++;
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingDictionary(Dictionary<string, OctopusImportResourceResultDto> inner) : IReadOnlyDictionary<string, OctopusImportResourceResultDto>
    {
        public int ValueReads { get; private set; }

        public IEnumerable<OctopusImportResourceResultDto> Values
        {
            get
            {
                ValueReads++;
                return inner.Values;
            }
        }

        public OctopusImportResourceResultDto this[string key] => inner[key];

        public IEnumerable<string> Keys => inner.Keys;

        public int Count => inner.Count;

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public bool TryGetValue(string key, out OctopusImportResourceResultDto value) => inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, OctopusImportResourceResultDto>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
