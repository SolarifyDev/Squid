using System.Diagnostics;
using System.IO;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Core.Services.Publishing.Recipes;

namespace Squid.UnitTests.Services.Publishing.Recipes;

public class ShellQuoteTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("", "''")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(id) `id` \"x\" ; | & * ? ~ !", "'$(id) `id` \"x\" ; | & * ? ~ !'")]
    public void Literal_WrapsInSingleQuotes_EscapingQuotes(string value, string expected)
    {
        ShellQuote.Literal("x", value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a#{b}")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void Literal_RefusesWhatCannotBeKeptLiteral(string value)
    {
        Should.Throw<RecipeInputInvalidException>(() => ShellQuote.Literal("name", value)).Input.ShouldBe("name");
    }

    [Theory]
    [InlineData("it's $(echo pwned) `echo pwned` \"q\" \\ ; | & * ? ~ ! #")]
    [InlineData("-n")]
    [InlineData("  spaced  ")]
    [InlineData("$HOME ${HOME} $((1+1))")]
    public void Literal_RoundTripsThroughRealBash(string value)
    {
        if (!File.Exists("/bin/bash")) return;

        var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true };

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"printf '%s' {ShellQuote.Literal("x", value)}");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        output.ShouldBe(value, "bash must read the quoted value back exactly, running nothing inside it");
    }
}
