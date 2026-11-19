using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.Secrets.ImportedSecretPreflightRulesTests;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>The D16 preflight service: reads only what it needs (nothing for a deployment without empty secrets) and names each owner.</summary>
public class ImportedSecretPreflightTests
{
    private const int SpaceId = 3;
    private static readonly VariableScopeContext Scope = new() { EnvironmentId = 11, EnvironmentName = "Production" };

    private readonly Mock<IOctopusImportSourceMapDataProvider> _sourceMap = new();
    private readonly Mock<IVariableDataProvider> _variables = new();
    private readonly Mock<IRepository> _repository = new();

    public ImportedSecretPreflightTests()
    {
        WithOwners([new Project { Name = "Web", VariableSetId = 601 }], [new LibraryVariableSet { Name = "Shared mail", VariableSetId = 602 }]);
        _variables.Setup(v => v.GetSensitiveVariableSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    [Fact]
    public async Task NoEmptySecret_NothingIsRead()
    {
        var missing = await Preflight().FindMissingAsync(SpaceId, [Secret("Db.Password", value: "cipher")], Scope);

        missing.ShouldBeEmpty();
        _sourceMap.VerifyNoOtherCalls();
        _variables.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmptySecretWithoutImportRow_OnlyTheRowsAreRead_NoBlock()
    {
        Rows(SetRow(601, "variableset-Projects-1"));

        var missing = await Preflight().FindMissingAsync(SpaceId, [Secret("Db.Password")], Scope);

        missing.ShouldBeEmpty();
        _sourceMap.Verify(s => s.GetByDestinationsAsync(SpaceId, It.Is<IReadOnlyCollection<string>>(t => t.SequenceEqual(ImportedSecretPreflightRules.SourceTypes)), It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 601 })), It.IsAny<CancellationToken>()));
        _variables.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingSecrets_NamedByOwner_ProjectAndLibrary_LiveSlotsOfTheirSets()
    {
        var project = Secret("Db.Password");
        var library = Secret("Smtp.Password", setId: 602);
        var orphan = Secret("Api.Key", setId: 603);
        Rows(SetRow(601, "variableset-Projects-1"), SetRow(602, "variableset-LibraryVariableSets-7"), SetRow(603, "variableset-Projects-9"), SecretRow(601, "variableset-Projects-1", project), SecretRow(602, "variableset-LibraryVariableSets-7", library), SecretRow(603, "variableset-Projects-9", orphan));

        var missing = await Preflight().FindMissingAsync(SpaceId, [project, library, orphan], Scope);

        missing.Select(m => (m.Name, m.OwnerName, m.Reason)).ShouldBe([("Db.Password", "project \"Web\"", MissingImportedSecretReason.NotImported), ("Smtp.Password", "library variable set \"Shared mail\"", MissingImportedSecretReason.NotImported), ("Api.Key", null, MissingImportedSecretReason.NotImported)]);
        _variables.Verify(v => v.GetSensitiveVariableSlotsAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { 601, 602, 603 })), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ValueSetSinceTheRelease_ReportedAsStaleSnapshot()
    {
        var secret = Secret("Db.Password");
        Rows(SetRow(601, "variableset-Projects-1"), SecretRow(601, "variableset-Projects-1", secret));
        _variables.Setup(v => v.GetSensitiveVariableSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([Slot(601, secret, true)]);

        var missing = await Preflight().FindMissingAsync(SpaceId, [secret], Scope);

        missing.Single().Reason.ShouldBe(MissingImportedSecretReason.SnapshotPredatesValue);
    }

    // ── Message ────────────────────────────────────────────────────────────

    [Fact]
    public void ExceptionMessage_NamesEveryVariableOwnerScopeAndWhatToDo_NeverAValue()
    {
        var exception = new ImportedSecretMissingException(42, [new MissingImportedSecret("Db.Password", 601, "Environment: Production", MissingImportedSecretReason.NotImported) { OwnerName = "project \"Web\"" }, new MissingImportedSecret("Smtp.Password", 602, "", MissingImportedSecretReason.SnapshotPredatesValue)]);

        exception.ShouldBeAssignableTo<DeploymentValidationException>();
        exception.DeploymentId.ShouldBe(42);
        exception.Secrets.Count.ShouldBe(2);
        exception.Message.ShouldSatisfyAllConditions(
            m => m.ShouldStartWith("Deployment 42 cannot start: 2 sensitive variable(s) imported from Octopus have no value in this deployment."),
            m => m.ShouldContain("- \"Db.Password\" (Environment: Production) in project \"Web\": secret not imported from Octopus"),
            m => m.ShouldContain("import the Octopus export file of the same space"),
            m => m.ShouldContain("empty in Octopus too"),
            m => m.ShouldContain("- \"Smtp.Password\" in variable set 602: a value has been set since this release was created"),
            m => m.ShouldContain("create a new release"));
    }

    private ImportedSecretPreflight Preflight() => new(_sourceMap.Object, _variables.Object, _repository.Object);

    private void Rows(params OctopusImportSourceMapRow[] rows)
        => _sourceMap.Setup(s => s.GetByDestinationsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(rows.ToList());

    private void WithOwners(Project[] projects, LibraryVariableSet[] libraries)
    {
        _repository.Setup(r => r.QueryNoTracking(It.IsAny<Expression<Func<Project, bool>>>())).Returns((Expression<Func<Project, bool>> p) => projects.AsQueryable().Where(p).BuildMock());
        _repository.Setup(r => r.QueryNoTracking(It.IsAny<Expression<Func<LibraryVariableSet, bool>>>())).Returns((Expression<Func<LibraryVariableSet, bool>> p) => libraries.AsQueryable().Where(p).BuildMock());
    }
}
