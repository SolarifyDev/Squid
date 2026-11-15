using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// D16 rules: which deployment variables are empty secrets an Octopus API import recorded as missing. Only a row for exactly the
/// variable's identity in its set counts; anything Squid users or export files created empty never blocks a deployment.
/// </summary>
public class ImportedSecretPreflightRulesTests
{
    private const int ProjectSet = 601;
    private const int LibrarySet = 602;
    private const int EnvironmentId = 11;
    private const int OtherEnvironmentId = 12;
    private const int ChannelId = 21;
    private const string ProjectSetSource = "variableset-Projects-1";
    private const string LibrarySetSource = "variableset-LibraryVariableSets-7";

    private static readonly VariableScopeContext Deployment = new() { EnvironmentId = EnvironmentId, EnvironmentName = "Production", ChannelId = ChannelId, ChannelName = "Default" };

    // ── Candidates ──────────────────────────────────────────────────────────

    public static TheoryData<string, VariableDto, bool> CandidateCases => new()
    {
        { "EmptySensitive", Secret("Db.Password"), true },
        { "NullValue", Secret("Db.Password", value: null), true },
        { "HasValue", Secret("Db.Password", value: "cipher"), false },
        { "NotSensitive", Plain("Db.Host", ""), false },
        { "NoVariableSet (built-in/output)", Secret("Db.Password", setId: 0), false },
        { "OwnEnvironment", Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{EnvironmentId}")]), true },
        { "OwnEnvironmentAmongOthers", Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{OtherEnvironmentId}"), (VariableScopeType.Environment, $"{EnvironmentId}")]), true },
        { "OtherEnvironment", Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{OtherEnvironmentId}")]), false },
        { "OwnChannel", Secret("Db.Password", scopes: [(VariableScopeType.Channel, $"{ChannelId}")]), true },
        { "OtherChannel", Secret("Db.Password", scopes: [(VariableScopeType.Channel, "99")]), false },
        { "TargetLevelScopesNeverExclude", Secret("Db.Password", scopes: [(VariableScopeType.Role, "web"), (VariableScopeType.Machine, "5"), (VariableScopeType.Action, "7"), (VariableScopeType.Process, "8")]), true },
        { "OwnEnvironmentPlusRole", Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{EnvironmentId}"), (VariableScopeType.Role, "web")]), true },
        { "OtherEnvironmentPlusRole", Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{OtherEnvironmentId}"), (VariableScopeType.Role, "web")]), false }
    };

    [Theory]
    [MemberData(nameof(CandidateCases))]
    public void Candidates_OnlyEmptyStoredSecretsTheDeploymentMayUse(string caseName, VariableDto variable, bool expected)
    {
        ImportedSecretPreflightRules.Candidates([variable], Deployment).Contains(variable).ShouldBe(expected, caseName);
    }

    // An unscoped empty secret overridden for this environment is never read; a target-scoped override may not apply to every target.
    public static TheoryData<string, VariableDto, bool> OverrideCases => new()
    {
        { "EnvironmentOverride", Plain("Db.Password", "set", (VariableScopeType.Environment, $"{EnvironmentId}")), false },
        { "ChannelOverride", Plain("Db.Password", "set", (VariableScopeType.Channel, $"{ChannelId}")), false },
        { "OverrideNameCaseInsensitive", Plain("DB.PASSWORD", "set", (VariableScopeType.Environment, $"{EnvironmentId}")), false },
        { "OverrideForOtherEnvironment", Plain("Db.Password", "set", (VariableScopeType.Environment, $"{OtherEnvironmentId}")), true },
        { "RoleScopedOverride", Plain("Db.Password", "set", (VariableScopeType.Role, "web")), true },
        { "EqualRankOverride", Plain("Db.Password", "set"), true },
        { "OtherName", Plain("Db.User", "set", (VariableScopeType.Environment, $"{EnvironmentId}")), true }
    };

    [Theory]
    [MemberData(nameof(OverrideCases))]
    public void Candidates_UnscopedSecret_DroppedOnlyWhenAValueForEveryTargetOutranksIt(string caseName, VariableDto other, bool expected)
    {
        var secret = Secret("Db.Password");

        ImportedSecretPreflightRules.Candidates([secret, other], Deployment).Contains(secret).ShouldBe(expected, caseName);
    }

    [Fact]
    public void Candidates_HigherRankedSecret_NotOutrankedByEnvironmentValue()
    {
        var secret = Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{EnvironmentId}"), (VariableScopeType.Role, "web")]);
        var other = Plain("Db.Password", "set", (VariableScopeType.Environment, $"{EnvironmentId}"));

        ImportedSecretPreflightRules.Candidates([secret, other], Deployment).ShouldBe([secret]);
    }

    [Fact]
    public void Candidates_NullVariables_Empty()
    {
        ImportedSecretPreflightRules.Candidates(null, Deployment).ShouldBeEmpty();
    }

    // ── Match ───────────────────────────────────────────────────────────────

    public static TheoryData<string, string, bool, MissingImportedSecretReason?> MatchCases => new()
    {
        { "MissingAndStillEmpty", OctopusImportSourceMapQueries.MissingOutcome, false, MissingImportedSecretReason.NotImported },
        { "MissingButSetSinceRelease", OctopusImportSourceMapQueries.MissingOutcome, true, MissingImportedSecretReason.SnapshotPredatesValue },
        { "FilledSinceRelease", OctopusImportSourceMapQueries.FilledOutcome, true, MissingImportedSecretReason.SnapshotPredatesValue },
        { "SupersededSinceRelease", OctopusImportSourceMapQueries.SupersededOutcome, true, MissingImportedSecretReason.SnapshotPredatesValue },
        { "FilledEmptyInOctopusToo", OctopusImportSourceMapQueries.FilledOutcome, false, null },
        { "SupersededThenClearedByUser", OctopusImportSourceMapQueries.SupersededOutcome, false, null }
    };

    [Theory]
    [MemberData(nameof(MatchCases))]
    public void Match_RowStateAndLiveValue_DecideTheReason(string caseName, string outcome, bool liveHasValue, MissingImportedSecretReason? expected)
    {
        var secret = Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{EnvironmentId}")]);

        var missing = Match([secret], [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, ProjectSetSource, secret, outcome)], [Slot(ProjectSet, secret, liveHasValue)]);

        missing.Select(m => (MissingImportedSecretReason?)m.Reason).SingleOrDefault().ShouldBe(expected, caseName);
    }

    public static TheoryData<string, OctopusImportSourceMapRow[]> UnmatchedRows => new()
    {
        { "NoRows", [] },
        { "OnlyTheSetRow", [SetRow(ProjectSet, ProjectSetSource)] },
        { "NoSetRow (key unprovable)", [SecretRow(ProjectSet, ProjectSetSource, Secret("Db.Password"))] },
        { "RowOfAnotherName", [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, ProjectSetSource, Secret("Db.User"))] },
        { "RowOfAnotherScope", [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, ProjectSetSource, Secret("Db.Password", scopes: [(VariableScopeType.Environment, $"{OtherEnvironmentId}")]))] },
        { "RowOfAnotherOwnerSet", [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, LibrarySetSource, Secret("Db.Password"))] },
        { "KeyOfAnotherSetsOwner", [SetRow(ProjectSet, ProjectSetSource), SetRow(LibrarySet, LibrarySetSource), SecretRow(ProjectSet, LibrarySetSource, Secret("Db.Password"))] },
        { "RowOnAnotherSquidSet", [SetRow(ProjectSet, ProjectSetSource), SetRow(LibrarySet, ProjectSetSource), SecretRow(LibrarySet, ProjectSetSource, Secret("Db.Password"))] }
    };

    [Theory]
    [MemberData(nameof(UnmatchedRows))]
    public void Match_NoRowForExactlyThisIdentityInThisSet_NeverBlocks(string caseName, OctopusImportSourceMapRow[] rows)
    {
        var secret = Secret("Db.Password");

        Match([secret], rows, [Slot(ProjectSet, secret, false)]).ShouldBeEmpty(caseName);
    }

    // Rows are stored normalised (upper case, export suffix stripped): the recomputed key is compared the same way.
    [Fact]
    public void Match_StoredNormalisedRows_StillMatch()
    {
        var secret = Secret("Db.Password");
        var rows = new[] { SetRow(ProjectSet, "VARIABLESET-PROJECTS-1"), SecretRow(ProjectSet, ProjectSetSource, secret) with { SourceId = SecretRow(ProjectSet, ProjectSetSource, secret).SourceId.ToUpperInvariant() } };

        Match([secret], rows, []).Single().Reason.ShouldBe(MissingImportedSecretReason.NotImported);
    }

    [Fact]
    public void Match_TwoSecretsInTwoSets_EachMatchedByItsOwnOwner()
    {
        var project = Secret("Db.Password");
        var library = Secret("Smtp.Password", setId: LibrarySet);
        var rows = new[] { SetRow(ProjectSet, ProjectSetSource), SetRow(LibrarySet, LibrarySetSource), SecretRow(ProjectSet, ProjectSetSource, project), SecretRow(LibrarySet, LibrarySetSource, library, OctopusImportSourceMapQueries.FilledOutcome) };

        Match([project, library], rows, [Slot(ProjectSet, project, false), Slot(LibrarySet, library, false)]).Select(m => m.Name).ShouldBe(["Db.Password"]);
    }

    // Two live variables share the identity: none counts as set, so a still-Missing row keeps blocking.
    [Fact]
    public void Match_AmbiguousLiveSlots_StillNotImported()
    {
        var secret = Secret("Db.Password");

        Match([secret], [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, ProjectSetSource, secret)], [Slot(ProjectSet, secret, true), Slot(ProjectSet, secret, false)]).Single().Reason.ShouldBe(MissingImportedSecretReason.NotImported);
    }

    [Fact]
    public void Match_ScopeSummary_NamesTheDeploymentsEnvironmentAndChannel_OthersAsStored()
    {
        var secret = Secret("Db.Password", scopes: [(VariableScopeType.Role, "web"), (VariableScopeType.Environment, $"{EnvironmentId}"), (VariableScopeType.Channel, $"{ChannelId}"), (VariableScopeType.Environment, $"{OtherEnvironmentId}")]);

        var missing = Match([secret], [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, ProjectSetSource, secret)], []).Single();

        missing.ShouldSatisfyAllConditions(m => m.Scope.ShouldBe($"Channel: Default; Environment: Production, {OtherEnvironmentId}; Role: web"), m => m.VariableSetId.ShouldBe(ProjectSet), m => m.Name.ShouldBe("Db.Password"));
    }

    [Fact]
    public void Match_Unscoped_EmptyScopeSummary()
    {
        var secret = Secret("Db.Password");

        Match([secret], [SetRow(ProjectSet, ProjectSetSource), SecretRow(ProjectSet, ProjectSetSource, secret)], []).Single().Scope.ShouldBe(string.Empty);
    }

    [Fact]
    public void SourceTypes_ArePinned()
    {
        ImportedSecretPreflightRules.SourceTypes.ShouldBe(["VariableSet", "VariableSecret"]);
        ImportedSecretPreflightRules.DeploymentLevelScopes.ShouldBe([VariableScopeType.Environment, VariableScopeType.Channel], ignoreOrder: true);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IReadOnlyList<MissingImportedSecret> Match(VariableDto[] candidates, OctopusImportSourceMapRow[] rows, SensitiveVariableSlot[] live)
        => ImportedSecretPreflightRules.Match(candidates, rows, live, Deployment);

    internal static VariableDto Secret(string name, string value = "", int setId = ProjectSet, params (VariableScopeType Type, string Value)[] scopes)
        => new() { Name = name, Value = value, Type = VariableType.Password, IsSensitive = true, VariableSetId = setId, Scopes = scopes.Select(s => new VariableScopeDto { ScopeType = s.Type, ScopeValue = s.Value }).ToList() };

    private static VariableDto Plain(string name, string value, params (VariableScopeType Type, string Value)[] scopes)
        => new() { Name = name, Value = value, Type = VariableType.String, VariableSetId = ProjectSet, Scopes = scopes.Select(s => new VariableScopeDto { ScopeType = s.Type, ScopeValue = s.Value }).ToList() };

    internal static OctopusImportSourceMapRow SetRow(int setId, string sourceId) => new(1, "VariableSet", sourceId, setId, OctopusImportSourceMapQueries.CreatedOutcome);

    // The key exactly as the server import writes it, through the import's own function over the Squid identity.
    internal static OctopusImportSourceMapRow SecretRow(int setId, string ownerSourceId, VariableDto variable, string outcome = OctopusImportSourceMapQueries.MissingOutcome)
        => new(1, OctopusImportSourceMapQueries.VariableSecretSourceType, OctopusImportSecretKeys.ForIdentity(ownerSourceId, Identity(variable)), setId, outcome);

    internal static SensitiveVariableSlot Slot(int setId, VariableDto variable, bool hasValue) => new(1, setId, Identity(variable), hasValue);

    private static VariableIdentity Identity(VariableDto variable) => VariableIdentity.Of(variable.Name, variable.Type, variable.Scopes.Select(s => (s.ScopeType, s.ScopeValue)));
}
