using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-9 — pure-function tests for <see cref="PropertiesConfigFormat"/>.
/// Java-properties + INI line-oriented rewrite: key-path lookup, INI
/// section prefixing, format preservation (comments / order / spacing /
/// EOL), Squid.* guard, line-continuation skip.
/// </summary>
public sealed class PropertiesConfigFormatTests
{
    [Theory]
    [InlineData("/x/y.properties", true)]
    [InlineData("/x/y.PROPERTIES", true)]
    [InlineData("/x/y.ini", true)]
    [InlineData("/x/y.json", false)]
    [InlineData("/x/y.yaml", false)]
    public void CanHandle_ByExtension(string path, bool expected)
        => new PropertiesConfigFormat().CanHandle(path).ShouldBe(expected);

    // ── .properties happy path ──────────────────────────────────────────────

    [Fact]
    public void Replace_DottedKey_ValueReplaced()
    {
        var props = "logging.level.root=INFO\n";
        var vars = NewVarSet(("logging.level.root", "DEBUG"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("logging.level.root=DEBUG");
        result.Output.ShouldNotContain("INFO");
    }

    [Fact]
    public void Replace_ColonDelimiter_AlsoSupported()
    {
        // Java properties accept key:value too.
        var props = "database.url : jdbc:old\n";

        // Note: the FIRST colon is the delimiter, so the key is "database.url"
        // and the value is " jdbc:old". Operator variable matches the key path.
        var vars = NewVarSet(("database.url", "jdbc:new"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("jdbc:new");
    }

    [Fact]
    public void Replace_ColonPathVariable_MatchesDottedKey()
    {
        // Operator writes the variable in ASP.NET-Core colon idiom; the file
        // key is dotted. ConfigVariableLookup bridges them.
        var props = "Logging.LogLevel.Default=Information\n";
        var vars = NewVarSet(("Logging:LogLevel:Default", "Debug"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("=Debug");
    }

    [Fact]
    public void Replace_PreservesSpacingAroundDelimiter()
    {
        var props = "my.key = oldvalue\n";
        var vars = NewVarSet(("my.key", "newvalue"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.Output.ShouldBe("my.key = newvalue\n",
            customMessage: "Spacing around the delimiter MUST be preserved — only the value text changes.");
    }

    // ── INI section prefixing ───────────────────────────────────────────────

    [Fact]
    public void Replace_IniSection_PrefixesKeyPath()
    {
        var ini = "[database]\nurl=old\n[logging]\nlevel=INFO\n";
        var vars = NewVarSet(("database.url", "newurl"), ("logging.level", "DEBUG"));

        var result = new PropertiesConfigFormat().Replace(ini, vars);

        result.ReplacedCount.ShouldBe(2);
        result.Output.ShouldContain("url=newurl");
        result.Output.ShouldContain("level=DEBUG");
        result.Output.ShouldContain("[database]");    // section headers preserved
        result.Output.ShouldContain("[logging]");
    }

    [Fact]
    public void Replace_IniSameKeyDifferentSections_DisambiguatedByPath()
    {
        var ini = "[a]\nport=1\n[b]\nport=2\n";
        var vars = NewVarSet(("b.port", "9090"));

        var result = new PropertiesConfigFormat().Replace(ini, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("[a]\nport=1");    // a.port untouched
        result.Output.ShouldContain("port=9090");      // b.port replaced
    }

    // ── Format preservation ─────────────────────────────────────────────────

    [Fact]
    public void Replace_PreservesCommentsAndBlankLinesAndOrder()
    {
        var props = "# header comment\n\nfirst.key=a\n! bang comment\nsecond.key=b\n";
        var vars = NewVarSet(("second.key", "B"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.Output.ShouldBe("# header comment\n\nfirst.key=a\n! bang comment\nsecond.key=B\n",
            customMessage: "Comments (# and !), blank lines, key order, and unmatched keys MUST all be byte-preserved.");
    }

    [Fact]
    public void Replace_PreservesCrlfEol()
    {
        var props = "a.b=old\r\nc.d=keep\r\n";
        var vars = NewVarSet(("a.b", "new"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.Output.ShouldBe("a.b=new\r\nc.d=keep\r\n",
            customMessage: "CRLF line endings MUST round-trip — Windows-authored .properties files keep their EOL style.");
    }

    [Fact]
    public void Replace_SemicolonComment_Preserved()
    {
        var ini = "; ini-style comment\nkey=old\n";
        var vars = NewVarSet(("key", "new"));

        var result = new PropertiesConfigFormat().Replace(ini, vars);

        result.Output.ShouldContain("; ini-style comment");
        result.Output.ShouldContain("key=new");
    }

    // ── Squid.* guard (shared) ──────────────────────────────────────────────

    [Fact]
    public void Replace_SquidNamespacedKey_DotForm_DoesNotClobber()
    {
        var props = "Squid.Deployment.Id=operator-literal\n";
        var vars = NewVarSet(("Squid.Deployment.Id", "runtime-guid"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldContain("operator-literal");
        result.Output.ShouldNotContain("runtime-guid");
    }

    // ── Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Replace_NoMatch_NoOp()
    {
        var props = "a.b=1\n";
        var result = new PropertiesConfigFormat().Replace(props, NewVarSet(("x.y", "z")));
        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldBe("a.b=1\n");
    }

    [Fact]
    public void Replace_KeyOnlyLineNoDelimiter_Untouched()
    {
        var props = "justakey\nreal.key=v\n";
        var result = new PropertiesConfigFormat().Replace(props, NewVarSet(("real.key", "w")));
        result.Output.ShouldContain("justakey");
        result.Output.ShouldContain("real.key=w");
    }

    [Fact]
    public void Replace_LineContinuation_SkippedToAvoidCorruption()
    {
        // A value ending with backslash is a Java multi-line value. We MUST
        // NOT partially rewrite it — leave the key intact.
        var props = "multi.key=part1\\\n  part2\nsimple.key=old\n";
        var vars = NewVarSet(("multi.key", "REPLACED"), ("simple.key", "new"));

        var result = new PropertiesConfigFormat().Replace(props, vars);

        result.Output.ShouldContain("multi.key=part1\\",
            customMessage: "Line-continuation values MUST be skipped (left intact) to avoid corrupting the continuation.");
        result.Output.ShouldNotContain("REPLACED");
        result.Output.ShouldContain("simple.key=new",
            customMessage: "Non-continuation keys in the same file still get replaced.");
    }

    [Fact]
    public void Replace_Empty_NoOpSuccess()
    {
        new PropertiesConfigFormat().Replace("", NewVarSet(("a", "b")))
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Replace_NullVariables_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new PropertiesConfigFormat().Replace("a=b", null!));
    }

    private static VariableSet NewVarSet(params (string name, string value)[] entries)
    {
        var set = new VariableSet();
        foreach (var (name, value) in entries) set.Set(name, value);
        return set;
    }
}
