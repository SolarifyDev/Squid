using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — pure-function tests for <see cref="XmlConfigFormat"/>. Covers
/// leaf-element + attribute paths, same-name-sibling indexing, Squid.*
/// namespace skip (shared via <see cref="ConfigVariableLookup"/>), and
/// the .xml-only extension scope (.config files stay XDT territory).
/// </summary>
public sealed class XmlConfigFormatTests
{
    [Theory]
    [InlineData("/x/y.xml", true)]
    [InlineData("/x/y.XML", true)]
    [InlineData("/x/y.config", false)]   // .config stays XDT (G1.2) territory
    [InlineData("/x/y.json", false)]
    [InlineData("/x/y.yaml", false)]
    public void CanHandle_OnlyDotXml(string path, bool expected)
        => new XmlConfigFormat().CanHandle(path).ShouldBe(expected);

    // ── Leaf-element text replacement ───────────────────────────────────────

    [Fact]
    public void Replace_LeafElement_TextContentReplaced()
    {
        var xml = """<?xml version="1.0"?><config><app><name>old</name></app></config>""";
        var vars = NewVarSet(("config.app.name", "new"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("<name>new</name>");
        result.Output.ShouldNotContain("<name>old</name>");
    }

    [Fact]
    public void Replace_AttributeValue_AddressableViaAtSigil()
    {
        // @attribute path — XPath-like convention so operators familiar
        // with XPath can guess paths without docs.
        var xml = """<?xml version="1.0"?><config><setting key="port" value="8080"/></config>""";
        var vars = NewVarSet(("config.setting.@value", "9090"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("value=\"9090\"");
    }

    [Fact]
    public void Replace_LeafContent_AND_Attribute_BothReplaced()
    {
        var xml = """<?xml version="1.0"?><config><setting attr="old-attr">old-text</setting></config>""";
        var vars = NewVarSet(
            ("config.setting", "new-text"),
            ("config.setting.@attr", "new-attr"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(2);
        result.Output.ShouldContain("new-attr");
        result.Output.ShouldContain("new-text");
    }

    // ── Same-name sibling indexing ──────────────────────────────────────────

    [Fact]
    public void Replace_SameNameSiblings_IndexedByPosition()
    {
        var xml = """<?xml version="1.0"?><items><item>a</item><item>b</item><item>c</item></items>""";
        var vars = NewVarSet(("items.item.1", "replaced"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("<item>a</item>");
        result.Output.ShouldContain("<item>replaced</item>");
        result.Output.ShouldContain("<item>c</item>");
    }

    [Fact]
    public void Replace_UniqueElement_NoIndexNeeded()
    {
        // Single child of a name doesn't get .0 suffix — path matches
        // bare element name. Same scheme as ASP.NET Core IConfiguration.
        var xml = """<?xml version="1.0"?><cfg><solo>old</solo></cfg>""";
        var vars = NewVarSet(("cfg.solo", "new"));

        var result = new XmlConfigFormat().Replace(xml, vars);
        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("<solo>new</solo>");
    }

    // ── Self-namespace guard (shared) ───────────────────────────────────────

    [Fact]
    public void Replace_SquidNamespacedVariable_DotForm_DoesNotClobber()
    {
        var xml = """<?xml version="1.0"?><Squid><Deployment><Id>operator-literal</Id></Deployment></Squid>""";
        var vars = NewVarSet(("Squid.Deployment.Id", "runtime-guid"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldContain("operator-literal");
        result.Output.ShouldNotContain("runtime-guid");
    }

    [Fact]
    public void Replace_SquidNamespacedVariable_ColonForm_StillReplaces_EscapeHatch()
    {
        var xml = """<?xml version="1.0"?><Squid><Custom><Field>placeholder</Field></Custom></Squid>""";
        var vars = NewVarSet(("Squid:Custom:Field", "operator-deliberate"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("operator-deliberate");
    }

    // ── Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Replace_NoVariables_SucceedsZeroReplaced()
    {
        var xml = """<?xml version="1.0"?><c><a>x</a></c>""";

        var result = new XmlConfigFormat().Replace(xml, NewVarSet());

        result.Succeeded.ShouldBeTrue();
        result.ReplacedCount.ShouldBe(0);
    }

    [Fact]
    public void Replace_MalformedXml_ReturnsFailure_NoCrash()
    {
        var result = new XmlConfigFormat().Replace("not really <xml", NewVarSet(("A", "x")));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Replace_PathHitsContainerNotLeaf_NoReplacement()
    {
        // Variable points at a container element; we should not match.
        var xml = """<?xml version="1.0"?><config><inner><leaf>x</leaf></inner></config>""";
        var vars = NewVarSet(("config.inner", "would-corrupt-schema"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldContain("<leaf>x</leaf>");
        result.Output.ShouldNotContain("would-corrupt-schema");
    }

    [Fact]
    public void Replace_NamespaceDeclarationsNotRewritten()
    {
        // xmlns:* declarations on elements MUST NOT be addressable via @
        // path — they're framework, not content. Operator's variable
        // can't accidentally clobber the schema namespace.
        var xml = """<?xml version="1.0"?><root xmlns:x="http://example.com/" xmlns="urn:y"><leaf>v</leaf></root>""";
        var vars = NewVarSet(("root.@xmlns:x", "evil"), ("root.@xmlns", "evil"));

        var result = new XmlConfigFormat().Replace(xml, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldContain("http://example.com/");
    }

    [Fact]
    public void Replace_NullVariables_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new XmlConfigFormat().Replace("<r/>", null!));
    }

    private static VariableSet NewVarSet(params (string name, string value)[] entries)
    {
        var set = new VariableSet();
        foreach (var (name, value) in entries) set.Set(name, value);
        return set;
    }
}
