using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

public class OctopusImportExportPasswordEvidenceTests
{
    private const string Password = "export-password";

    [Fact]
    public void EncryptedValues_AreExactlyTheValuesTheImportConsumersDecrypt()
    {
        // Parity pin: a node is evidence iff the import consumers' own predicates (OctopusImportSensitiveValues for
        // variables, OctopusImportFeedCredentials for feeds) resolve it with the right password and not with a wrong one.
        var nodes = new[]
        {
            Node("v-flag", OctopusResourceKind.Variable, new OctopusVariableDto { Name = "A", IsSensitive = true, Type = "String", Value = Cipher("a") }),
            Node("v-type", OctopusResourceKind.Variable, new OctopusVariableDto { Name = "B", Type = " sensitive ", Value = Cipher("b") }),
            Node("v-plain", OctopusResourceKind.Variable, new OctopusVariableDto { Name = "C", Type = "String", Value = Cipher("c") }),
            Node("v-masked", OctopusResourceKind.Variable, new OctopusVariableDto { Name = "D", IsSensitive = true, Value = "********" }),
            Node("v-empty", OctopusResourceKind.Variable, new OctopusVariableDto { Name = "E", IsSensitive = true, Value = null }),
            Node("f-secret", OctopusResourceKind.Feed, new OctopusFeedDto { Name = "ACR", Username = "robot", Password = Cipher("token") }),
            Node("f-anonymous", OctopusResourceKind.Feed, new OctopusFeedDto { Name = "Hub" }),
            Node("account", OctopusResourceKind.Account, JsonSerializer.SerializeToElement(new { Password = Cipher("account") })),
            Node("certificate", OctopusResourceKind.Certificate, JsonSerializer.SerializeToElement(new { Password = Cipher("cert") })),
            Node("template", OctopusResourceKind.ActionTemplate, new OctopusActionTemplateDto { Properties = new Dictionary<string, string> { ["Password"] = Cipher("param") } })
        };

        var evidence = OctopusImportExportPasswordEvidence.EncryptedValues(new OctopusResourceGraph(nodes, [], [], [])).ToHashSet();
        var decryptedByImport = nodes.Where(ImportDecryptsWithThePasswordOnly).Select(EncryptedValueOf).ToHashSet();

        evidence.ShouldBe(decryptedByImport, ignoreOrder: true);
        evidence.Count.ShouldBe(3);
    }

    [Fact]
    public void EncryptedValues_NullGraph_IsEmpty()
    {
        OctopusImportExportPasswordEvidence.EncryptedValues(null).ShouldBeEmpty();
    }

    private static bool ImportDecryptsWithThePasswordOnly(OctopusResourceNode node)
        => Resolvable(node, OctopusExportSecrets.FromPassword(Password)) && !Resolvable(node, OctopusExportSecrets.FromPassword("wrong")) && !Resolvable(node, OctopusExportSecrets.None);

    private static bool Resolvable(OctopusResourceNode node, OctopusExportSecrets secrets) => node.Kind switch
    {
        OctopusResourceKind.Variable => OctopusImportRequiredInputBuilder.IsSensitiveVariable(node.GetSource<OctopusVariableDto>()) && OctopusImportSensitiveValues.IsResolvable(node.GetSource<OctopusVariableDto>(), secrets),
        OctopusResourceKind.Feed => OctopusImportFeedCredentials.IsResolvable(node.GetSource<OctopusFeedDto>(), secrets),
        _ => false
    };

    private static string EncryptedValueOf(OctopusResourceNode node)
        => node.Kind == OctopusResourceKind.Feed ? node.GetSource<OctopusFeedDto>().Password : node.GetSource<OctopusVariableDto>().Value;

    private static string Cipher(string plaintext) => OctopusExportTestCipher.Encrypt(plaintext, Password);

    private static OctopusResourceNode Node(string sourceId, OctopusResourceKind kind, object source)
        => new(sourceId, sourceId, kind, OctopusDocumentKind.Project, $"{sourceId}.json", "Projects-1", null, false, source);
}
