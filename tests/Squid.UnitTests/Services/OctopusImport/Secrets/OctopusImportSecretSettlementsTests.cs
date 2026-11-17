using System.Collections.Generic;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>A secret an import left Missing is the user's the moment a Squid save gives it a value: only those rows of the saved set settle.</summary>
public class OctopusImportSecretSettlementsTests
{
    private const int SetId = 40;
    private const string OwnerSetSourceId = "variableset-Projects-1";

    private static readonly VariableIdentity DbPassword = VariableIdentity.Of("Db.Password", VariableType.Password, [(VariableScopeType.Environment, "3")]);
    private static readonly VariableIdentity ApiKey = VariableIdentity.Of("ApiKey", VariableType.Password, []);

    private static string KeyOf(VariableIdentity identity) => OctopusImportSecretKeys.ForIdentity(OwnerSetSourceId, identity);

    [Theory]
    [InlineData(true, OctopusImportSourceMapQueries.MissingOutcome, true)]     // set in Squid → settled
    [InlineData(false, OctopusImportSourceMapQueries.MissingOutcome, false)]   // still empty → stays Missing, an import may fill it
    [InlineData(true, OctopusImportSourceMapQueries.FilledOutcome, false)]     // already settled → untouched
    [InlineData(true, OctopusImportSourceMapQueries.SupersededOutcome, false)]
    public void SetInSquid_OnlyMissingRowsOfSecretsHoldingAValue(bool hasValue, string outcome, bool settled)
    {
        var rows = new List<OctopusImportSourceMapRow> { SetRow(SetId), SecretRow(KeyOf(DbPassword), SetId, outcome) };

        var result = OctopusImportSecretSettlements.SetInSquid(rows, [new SensitiveVariableSlot(1, SetId, DbPassword, hasValue)], SetId);

        result.Count.ShouldBe(settled ? 1 : 0);
    }

    [Fact]
    public void SetInSquid_OtherSecretOrOtherSet_NeverSettled()
    {
        var rows = new List<OctopusImportSourceMapRow> { SetRow(SetId), SecretRow(KeyOf(DbPassword), SetId, OctopusImportSourceMapQueries.MissingOutcome), SecretRow(KeyOf(ApiKey), SetId + 1, OctopusImportSourceMapQueries.MissingOutcome) };
        var slots = new List<SensitiveVariableSlot> { new(1, SetId, ApiKey, true), new(2, SetId + 1, ApiKey, true), new(3, SetId, DbPassword, false) };

        OctopusImportSecretSettlements.SetInSquid(rows, slots, SetId).ShouldBeEmpty();
    }

    private static OctopusImportSourceMapRow SetRow(int setId) => new(1, "VariableSet", OwnerSetSourceId, setId, OctopusImportSourceMapQueries.CreatedOutcome, null, null);

    private static OctopusImportSourceMapRow SecretRow(string key, int setId, string outcome) => new(1, OctopusImportSourceMapQueries.VariableSecretSourceType, key, setId, outcome, null, null);
}
