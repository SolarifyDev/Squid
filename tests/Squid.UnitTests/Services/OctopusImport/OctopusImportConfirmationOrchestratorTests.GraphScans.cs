using System.Collections;
using System.Linq;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;
using Squid.UnitTests.Services.OctopusImport.Secrets;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Confirmation runs inside the import transaction under the space lock, and the graph holds the whole source (history included):
/// per-secret work never scans it. The number of full passes over the graph does not grow with the number of withheld values,
/// their scope values or the secrets filled.
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    [Fact]
    public async Task MissingSensitiveValues_GraphScansDoNotGrowWithScopeValues()
    {
        var few = await GraphScansListingAWithheldValueAsync(scopeValues: 1);
        var many = await GraphScansListingAWithheldValueAsync(scopeValues: 40);

        many.ShouldBe(few, "naming scope values and the variable's owner must use an index, not a pass over the graph per value");
    }

    [Fact]
    public async Task SecretFills_GraphScansDoNotGrowWithTheFillsMade()
    {
        var one = await GraphScansFillingAsync(secondFill: false);
        var two = await GraphScansFillingAsync(secondFill: true);

        two.ShouldBe(one, "the installation evidence is fixed for the confirmation and evaluated once, not once per fill");
    }

    // Variables Octopus added to a set imported earlier are inserted one by one. Each insert looks up its set (and its owner's rows) by
    // key: a pass over the whole graph (history included) per inserted variable would make re-imports quadratic.
    [Fact]
    public async Task ChildrenInsertedIntoAnImportedOwner_GraphScansDoNotGrowWithTheInserts()
    {
        var one = await GraphScansInsertingVariablesAsync(count: 1);
        var many = await GraphScansInsertingVariablesAsync(count: 12);

        many.ShouldBe(one, "an inserted child must find its owner through an index, not a pass over the graph per insert");
    }

    private async Task<int> GraphScansInsertingVariablesAsync(int count)
    {
        var nodes = BuildRichNodes();
        var added = Enumerable.Range(1, count).Select(n => new OctopusResourceNode($"Variables-added-{n}", $"Added{n}", OctopusResourceKind.Variable, OctopusDocumentKind.VariableSet, "added.json", "Projects-1", nodes.VariableSet.SourceId, false, new OctopusVariableDto { Id = $"Variables-added-{n}", Name = $"Added{n}", Type = "String", Value = "v" })).ToList();
        var rows = new List<(string, string, int, string)> { (nameof(OctopusResourceKind.Project), nodes.Project.SourceId, 1001, "Created"), (nameof(OctopusResourceKind.VariableSet), nodes.VariableSet.SourceId, 1002, "Created") };
        var resources = new[] { nodes.Project, nodes.VariableSet }.Concat(added).ToArray();
        var preview = Plan(new[] { (nodes.Project, OctopusImportPreviewAction.AlreadyImported, (int?)1001), (nodes.VariableSet, OctopusImportPreviewAction.AlreadyImported, 1002) }.Concat(added.Select(n => (n, OctopusImportPreviewAction.Create, (int?)null))).ToArray());
        var inserter = new OwnerFindingInserter();
        var harness = CreateHarness(nodes, BuildDependencyPlan(resources), preview, true, false, false, sourceMap: SourceMap(PriorWith(rows)).Object, childInserters: [inserter]);
        var (request, graph) = Counted(harness.Request);

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, string.Join("; ", harness.SessionService.RecordedResult.Resources.SelectMany(r => r.Diagnostics).Select(d => d.Message)));
        inserter.Inserted.ShouldBe(count);

        return graph.Enumerations;
    }

    // Looks its owner up the way every real inserter does, and writes nothing.
    private sealed class OwnerFindingInserter : Squid.Core.Services.OctopusImport.ChildInsert.IOctopusImportChildInserter
    {
        public int Inserted { get; private set; }

        public OctopusResourceKind Kind => OctopusResourceKind.Variable;

        public Task<IReadOnlyDictionary<int, int>> SiblingOrdersAsync(int ownerId, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

        public Task<OctopusImportInsertedChild> InsertAsync(Squid.Core.Services.OctopusImport.ChildInsert.OctopusImportChildInsert insert, CancellationToken ct)
        {
            insert.Find(insert.Resource.ParentSourceId, OctopusResourceKind.VariableSet).ShouldNotBeNull();
            Inserted++;

            return Task.FromResult(new OctopusImportInsertedChild(5000 + Inserted, [], new Dictionary<string, int>()));
        }
    }

    private async Task<int> GraphScansListingAWithheldValueAsync(int scopeValues)
    {
        var nodes = BuildRichNodes();
        WithholdValue(nodes.VariableOne);
        nodes.VariableOne.GetSource<OctopusVariableDto>().Scope["Role"] = Enumerable.Range(1, scopeValues).Select(n => $"web-{n}").ToList();
        var harness = CreateHarness(nodes, BuildDependencyPlan(nodes.RichOrderedResources), BuildPreviewPlan(nodes.RichOrderedResources, reusedResources: [nodes.Environment]), true, false, false, sourceMap: SourceMap(OctopusImportPriorMappings.Empty).Object, secretsAccessor: Session(true));
        var (request, graph) = Counted(harness.Request);

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.SessionService.RecordedResult.MissingSensitiveValues.ShouldHaveSingleItem().ScopeSummary.ShouldContain($"web-{scopeValues}");

        return graph.Enumerations;
    }

    private async Task<int> GraphScansFillingAsync(bool secondFill)
    {
        var nodes = BuildRichNodes();
        var sensitive = secondFill ? new[] { nodes.VariableOne, nodes.VariableTwo } : [nodes.VariableOne];
        var rows = new List<(string, string, int, string)> { (nameof(OctopusResourceKind.Project), nodes.Project.SourceId, 1001, "Created"), (nameof(OctopusResourceKind.VariableSet), nodes.VariableSet.SourceId, 1002, "Created") };

        foreach (var node in sensitive)
        {
            var variable = node.GetSource<OctopusVariableDto>();
            (variable.Type, variable.IsSensitive, variable.Value) = ("Sensitive", true, OctopusExportTestCipher.Encrypt($"{node.SourceId}-secret", FillPassword));
            rows.Add((OctopusImportSourceMapQueries.VariableSecretSourceType, OctopusImportSecretKeys.ForVariable(nodes.VariableSet.SourceId, variable, (_, _) => null), 1002, OctopusImportSourceMapQueries.MissingOutcome));
        }

        var variables = new Mock<IVariableService>();
        variables.Setup(v => v.FillEmptySensitiveValueAsync(1002, It.IsAny<VariableIdentity>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SecretSlotState.Filled);
        var resources = new[] { nodes.Project, nodes.VariableSet }.Concat(sensitive).ToArray();
        var preview = Plan(new[] { (nodes.Project, OctopusImportPreviewAction.AlreadyImported, (int?)1001), (nodes.VariableSet, OctopusImportPreviewAction.AlreadyImported, 1002) }.Concat(sensitive.Select(n => (n, OctopusImportPreviewAction.FillMissingSecret, (int?)null))).ToArray());
        var harness = CreateHarness(nodes, BuildDependencyPlan(resources), preview, true, false, false, sourceMap: SourceMap(PriorWith(rows)).Object, secretsAccessor: FileSession(FillPassword), variableService: variables.Object, externalFeedService: Mock.Of<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedService>(), graphInstallationId: InstallationId);
        var (request, graph) = Counted(harness.Request);

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, string.Join("; ", harness.SessionService.RecordedResult.Resources.SelectMany(r => r.Diagnostics).Select(d => d.Message)));
        sensitive.ShouldAllBe(n => Outcome(harness, n).OutcomeState == OctopusImportResourceOutcomeState.SecretFilled);

        return graph.Enumerations;
    }

    private static (OctopusImportConfirmationRequest Request, CountingList<OctopusResourceNode> Graph) Counted(OctopusImportConfirmationRequest request)
    {
        var counted = new CountingList<OctopusResourceNode>(request.Graph.Resources);

        return (request with { Graph = request.Graph with { Resources = counted } }, counted);
    }

    private sealed class CountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int Enumerations { get; private set; }

        public T this[int index] => inner[index];

        public int Count => inner.Count;

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;

            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
