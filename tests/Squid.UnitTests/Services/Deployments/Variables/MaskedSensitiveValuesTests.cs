using System.Collections.Generic;
using System.Linq;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// The Variables page reads every sensitive value as the mask and posts the whole set back as read. A save keeps the value Squid
/// stores for each masked secret — an empty one (withheld by the Octopus API) stays empty so a later fill and the deployment
/// preflight still see it — and never stores the mask itself.
/// </summary>
public class MaskedSensitiveValuesTests
{
    private const int SetId = 41;

    [Fact]
    public void Mask_LiteralPinned_AndIsWhatAReadReturns()
    {
        MaskedSensitiveValues.Mask.ShouldBe("***SENSITIVE***");

        new SensitiveVariableHandler().MaskSensitiveValue(new VariableDto { IsSensitive = true, Value = "" }).Value.ShouldBe(MaskedSensitiveValues.Mask);
    }

    [Theory]
    [InlineData("stored-secret", "stored-secret")]
    [InlineData("", "")]       // withheld by the API: stays empty, the fill and the preflight still see it
    [InlineData(null, null)]
    public void Restore_MaskedSecret_GetsTheStoredValueOfTheSameVariable(string storedValue, string expected)
    {
        var incoming = new List<VariableModel> { Secret("Db.Password", MaskedSensitiveValues.Mask, "3") };

        MaskedSensitiveValues.Restore(incoming, [Stored("Db.Password", storedValue, "3"), Stored("Db.Password", "other-env", "4")]);

        incoming.Single().Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("typed-by-user", true, "typed-by-user")]                    // a new value typed in the page is saved
    [InlineData(MaskedSensitiveValues.Mask, false, MaskedSensitiveValues.Mask)] // not sensitive: the text is the user's value
    public void Restore_ValueThatIsNotAMaskedSecret_IsSavedAsPosted(string posted, bool sensitive, string expected)
    {
        var incoming = new List<VariableModel> { new() { Name = "Db.Password", Type = VariableType.Password, IsSensitive = sensitive, Value = posted, Scopes = Scopes("3") } };

        MaskedSensitiveValues.Restore(incoming, [Stored("Db.Password", "stored-secret", "3")]);

        incoming.Single().Value.ShouldBe(expected);
    }

    // The scope of the only secret with that name was edited in the page: it is still that secret.
    [Fact]
    public void Restore_ScopeEdited_FallsBackToTheOnlyUnclaimedSecretOfThatName()
    {
        var incoming = new List<VariableModel> { Secret("Db.Password", MaskedSensitiveValues.Mask, "5") };

        MaskedSensitiveValues.Restore(incoming, [Stored("Db.Password", "stored-secret", "3")]);

        incoming.Single().Value.ShouldBe("stored-secret");
    }

    [Theory]
    [InlineData("3,4")]   // two candidates by name: which one is meant cannot be told
    [InlineData("")]      // no stored secret of that name (e.g. a duplicated row renamed)
    public void Restore_NoSingleStoredSecret_SavesEmpty_NeverTheMask(string storedEnvironmentList)
    {
        var incoming = new List<VariableModel> { Secret("Db.Password", MaskedSensitiveValues.Mask, "5") };

        var storedEnvironments = storedEnvironmentList.Split(',', StringSplitOptions.RemoveEmptyEntries);

        MaskedSensitiveValues.Restore(incoming, storedEnvironments.Select(e => Stored("Db.Password", $"value-{e}", e)).ToList());

        incoming.Single().Value.ShouldBe(string.Empty);
    }

    // An exact identity match is claimed first, so a scope-edited sibling cannot take its value.
    [Fact]
    public void Restore_ExactMatchesClaimedBeforeTheNameFallback()
    {
        var incoming = new List<VariableModel> { Secret("Db.Password", MaskedSensitiveValues.Mask, "9"), Secret("Db.Password", MaskedSensitiveValues.Mask, "3"), Secret("Db.Password", MaskedSensitiveValues.Mask, "3") };

        MaskedSensitiveValues.Restore(incoming, [Stored("Db.Password", "env-3-first", "3"), Stored("Db.Password", "env-4", "4"), Stored("Db.Password", "env-3-second", "3")]);

        incoming.Select(v => v.Value).ShouldBe(["env-4", "env-3-first", "env-3-second"]);
    }

    // ── VariableService seam ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateVariableSet_MaskedSecretPostedBack_StoresTheStoredValue_NotTheMask()
    {
        var (service, dataProvider, scopes) = Service();
        dataProvider.Setup(p => p.GetVariablesByVariableSetIdAsync(SetId, It.IsAny<CancellationToken>())).ReturnsAsync([new Variable { Id = 7, VariableSetId = SetId, Name = "Db.Password", Type = VariableType.Password, IsSensitive = true, Value = "" }, new Variable { Id = 8, VariableSetId = SetId, Name = "Plain", Type = VariableType.String, Value = "x" }]);
        scopes.Setup(p => p.GetVariableScopesByVariableIdsAsync(It.Is<List<int>>(ids => ids.SequenceEqual(new[] { 7 })), It.IsAny<CancellationToken>())).ReturnsAsync([new VariableScope { VariableId = 7, ScopeType = VariableScopeType.Environment, ScopeValue = "3" }]);
        var saved = new List<Variable>();
        dataProvider.Setup(p => p.AddVariablesAsync(SetId, It.IsAny<List<Variable>>(), It.IsAny<CancellationToken>())).Callback<int, List<Variable>, CancellationToken>((_, variables, _) => saved.AddRange(variables));

        await service.UpdateVariableSetAsync(Command(Secret("Db.Password", MaskedSensitiveValues.Mask, "3"), new VariableModel { Name = "Plain", Type = VariableType.String, Value = "edited" }), CancellationToken.None);

        saved.Select(v => (v.Name, v.Value)).ShouldBe([("Db.Password", ""), ("Plain", "edited")]);
        dataProvider.Verify(p => p.DeleteVariablesByVariableSetIdAsync(SetId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateVariableSet_NothingMasked_ReadsNoStoredValues()
    {
        var (service, dataProvider, _) = Service();

        await service.UpdateVariableSetAsync(Command(Secret("Db.Password", "typed", "3")), CancellationToken.None);

        dataProvider.Verify(p => p.GetVariablesByVariableSetIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // An import's fill not yet committed holds its rows: a save that read them unlocked would re-insert the masked secret empty.
    [Fact]
    public async Task UpdateVariableSet_ReadsAndRewritesTheSetOnlyUnderItsLock()
    {
        var (service, dataProvider, _) = Service();
        dataProvider.Reset();

        await service.UpdateVariableSetAsync(Command(Secret("Db.Password", MaskedSensitiveValues.Mask, "3")), CancellationToken.None);

        dataProvider.Verify(p => p.ExecuteWithVariableSetLockedAsync(SetId, It.IsAny<Func<CancellationToken, Task<VariableSetDto>>>(), It.IsAny<CancellationToken>()), Times.Once);
        dataProvider.Verify(p => p.DeleteVariablesByVariableSetIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateVariableSet_SettlesTheImportedSecretsTheSaveGaveAValue()
    {
        var (_, dataProvider, scopes) = Service();
        var settlement = new Mock<IVariableSetSavedHook>();
        var service = new VariableService(MapperFor(), dataProvider.Object, scopes.Object, new Mock<ILibraryVariableSetDataProvider>().Object, new VariableSecretCollaborators(new SensitiveVariableHandler(), savedHooks: [settlement.Object]));
        var command = Command(Secret("Db.Password", "k1", "3"));
        command.SpaceId = 5;

        await service.UpdateVariableSetAsync(command, CancellationToken.None);

        settlement.Verify(s => s.OnVariableSetSavedAsync(5, SetId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (IVariableService Service, Mock<IVariableDataProvider> DataProvider, Mock<IVariableScopeDataProvider> Scopes) Service()
    {
        var dataProvider = new Mock<IVariableDataProvider>();
        var scopes = new Mock<IVariableScopeDataProvider>();

        dataProvider.Setup(p => p.GetVariableSetByIdAsync(SetId, It.IsAny<CancellationToken>())).ReturnsAsync(new VariableSet { Id = SetId, OwnerType = VariableSetOwnerType.Project });
        scopes.Setup(p => p.GetVariableScopesByVariableIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        RunLockedActions(dataProvider);

        return (new VariableService(MapperFor(), dataProvider.Object, scopes.Object, new Mock<ILibraryVariableSetDataProvider>().Object, new VariableSecretCollaborators(new SensitiveVariableHandler())), dataProvider, scopes);
    }

    private static IMapper MapperFor()
    {
        var mapper = new Mock<IMapper>();

        mapper.Setup(m => m.Map<Variable>(It.IsAny<VariableModel>())).Returns<VariableModel>(m => new Variable { Name = m.Name, Value = m.Value, Type = m.Type, IsSensitive = m.IsSensitive });
        mapper.Setup(m => m.Map<VariableScope>(It.IsAny<VariableScopeModel>())).Returns<VariableScopeModel>(m => new VariableScope { ScopeType = m.ScopeType, ScopeValue = m.ScopeValue });
        mapper.Setup(m => m.Map<VariableSetDto>(It.IsAny<VariableSet>())).Returns(new VariableSetDto());

        return mapper.Object;
    }

    // The real provider takes the set's row locks around the save; the mock just runs it.
    public static void RunLockedActions(Mock<IVariableDataProvider> dataProvider)
        => dataProvider.Setup(p => p.ExecuteWithVariableSetLockedAsync(It.IsAny<int>(), It.IsAny<Func<CancellationToken, Task<VariableSetDto>>>(), It.IsAny<CancellationToken>())).Returns((int _, Func<CancellationToken, Task<VariableSetDto>> action, CancellationToken ct) => action(ct));

    private static UpdateVariableSetCommand Command(params VariableModel[] variables)
        => new() { Id = SetId, Name = "Project variables", OwnerType = VariableSetOwnerType.Project, Variables = variables.ToList() };

    private static VariableModel Secret(string name, string value, string environment)
        => new() { Name = name, Type = VariableType.Password, IsSensitive = true, Value = value, Scopes = Scopes(environment) };

    private static List<VariableScopeModel> Scopes(string environment) => [new() { ScopeType = VariableScopeType.Environment, ScopeValue = environment }];

    private static (VariableIdentity Identity, string Value) Stored(string name, string value, string environment)
        => (VariableIdentity.Of(name, VariableType.Password, [(VariableScopeType.Environment, environment)]), value);
}
