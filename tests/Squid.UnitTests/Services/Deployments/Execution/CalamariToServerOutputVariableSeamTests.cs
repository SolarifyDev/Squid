using System.Linq;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.UnitTests.Support;
using CalamariOutputProcessor = Squid.Calamari.Execution.ScriptOutputProcessor;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Pins the SEAM between Calamari's stdout and the server's output-variable capture — the join
/// the bug actually lived in.
///
/// <para>Each side was individually correct and individually tested: Calamari collected the
/// variable for its own conventions, and the server parsed service messages out of log lines
/// faultlessly. Nothing asserted that what Calamari <i>writes</i> is what the server can
/// <i>read</i>, so a suppression on the Calamari side dropped every output variable a
/// Calamari-run script emitted and no test noticed.</para>
///
/// <para>These drive the real <c>Squid.Calamari.Execution.ScriptOutputProcessor</c>, capture the
/// bytes it puts on stdout (exactly what the Tentacle collects into the script's log lines), and
/// feed those captured lines to the production server-side
/// <see cref="ServiceMessageParser.ParseOutputVariables"/>. No hand-written line in between — if
/// either side changes its wire format independently, these fail.</para>
///
/// <para>Member of <see cref="GlobalStateSerialisedCollection"/>: redirecting
/// <c>Console.Out</c> mutates a process global, so parallel classes would capture each
/// other's writes.</para>
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class CalamariToServerOutputVariableSeamTests
{
    private static readonly ServiceMessageParser ServerParser = new();

    [Fact]
    public void AVariableSetByACalamariRunScript_ReachesTheServer()
    {
        var captured = RunThroughCalamari("##squid[setVariable name='Url' value='https://web.test']");

        var serverSaw = ServerParser.ParseOutputVariables(captured);

        serverSaw.ShouldContainKey("Url",
            customMessage: "Calamari wrote the service message somewhere, but not to stdout — which is the only " +
                           "channel the Tentacle collects and therefore the server's only source of output " +
                           "variables. This is the exact failure the seam exists to catch.");
        serverSaw["Url"].Value.ShouldBe("https://web.test");
    }

    [Fact]
    public void TheSensitiveFlagSurvivesTheCrossing()
    {
        // A sensitive variable that arrives unflagged is worse than one that never arrives: the
        // server would treat the secret as ordinary and write it to task logs in plaintext.
        var captured = RunThroughCalamari("##squid[setVariable name='DbPassword' value='hunter2' sensitive='True']");

        var serverSaw = ServerParser.ParseOutputVariables(captured);

        serverSaw.ShouldContainKey("DbPassword");
        serverSaw["DbPassword"].IsSensitive.ShouldBeTrue(
            "a secret that crosses the seam unflagged gets logged in plaintext");
        serverSaw["DbPassword"].Value.ShouldBe("hunter2");
    }

    [Fact]
    public void TheBase64WireFormatCrossesUnmangled()
    {
        // The server accepts a double-quoted base64 value (used for anything containing quotes or
        // newlines); Calamari's own parser only recognises the single-quoted plaintext form. So
        // this line is, to Calamari, an ordinary log line — and it must still arrive byte-exact.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("value with 'quotes' and\nnewline"));

        var captured = RunThroughCalamari($"##squid[setVariable name=\"{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Multiline"))}\" value=\"{encoded}\"]");

        var serverSaw = ServerParser.ParseOutputVariables(captured);

        serverSaw.ShouldContainKey("Multiline",
            customMessage: "Calamari must forward lines it does not itself recognise byte-exact; anything that " +
                           "rewrites or re-quotes stdout breaks the server's base64 wire format.");
        serverSaw["Multiline"].Value.ShouldBe("value with 'quotes' and\nnewline");
    }

    [Fact]
    public void CalamariStillCollectsTheVariableForItsOwnConventions()
    {
        // The two consumers are independent and both matter: this one feeds a later Calamari
        // convention in the same run (PostDeploy reading what the main script computed). Fixing
        // the server side by moving collection to stdout-only would silently break it.
        var processor = new CalamariOutputProcessor();

        CaptureStdout(() => processor.ProcessLine("##squid[setVariable name='Url' value='https://web.test']"));

        processor.OutputVariables.ShouldContain(v => v.Name == "Url" && v.Value == "https://web.test");
    }

    [Fact]
    public void AnOrdinaryLogLineDoesNotBecomeAVariable()
    {
        // Control: proves the tests above pass because the seam carries service messages, not
        // because the server manufactures a variable from any line Calamari happens to print.
        var captured = RunThroughCalamari("Deploying release 1.2.3 to production");

        ServerParser.ParseOutputVariables(captured).ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Puts <paramref name="scriptLine"/> through the real Calamari output pipeline and returns
    /// the stdout lines it produced — i.e. precisely what the Tentacle hands the server.
    /// </summary>
    private static string[] RunThroughCalamari(string scriptLine)
    {
        var processor = new CalamariOutputProcessor();

        var stdout = CaptureStdout(() => processor.ProcessLine(scriptLine));

        return stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
    }

    private static string CaptureStdout(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new System.IO.StringWriter();

        try
        {
            Console.SetOut(writer);
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString();
    }
}
