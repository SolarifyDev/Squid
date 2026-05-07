using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.D.1+ — Section D E2E coverage for operator-diagnostic
/// commands (<c>show-thumbprint</c>, <c>list-instances</c>). These are
/// the commands operators tail to verify their setup against
/// server-side state (trust list, multi-instance roster).
///
/// <para>Tier 🟢 H (Rule 12.4): real production binary + real
/// <c>/etc/squid-tentacle/</c> filesystem state + real round-trip
/// against <see cref="LinuxStubSquidServer"/> for the show-thumbprint
/// path. No mocks at OS-resource layer.</para>
///
/// <para><b>Why diagnostic commands matter to ship-confidence</b>: when
/// an operator's agent fails to poll, the documented debugging recipe is:
/// (a) run <c>show-thumbprint</c> on the agent, (b) confirm that string
/// matches the server's trust-list entry. If the binary's
/// <c>show-thumbprint</c> ever prints a thumbprint that doesn't match
/// what its own register call sent to the server, EVERY operator's
/// debugging session is poisoned — they fix what they think is wrong
/// (the trust list) but the actual bug is elsewhere. This test pins the
/// round-trip identity contract.</para>
///
/// <para>Likewise <c>list-instances</c> is the multi-instance roster
/// view; if it doesn't show what <c>create-instance</c> just persisted,
/// operators have no way to verify their multi-instance setup short of
/// catting JSON.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxDiagnosticCommandE2ETests
{
    // ========================================================================
    // D1.h-Linux — `show-thumbprint` after register returns the SAME
    //               thumbprint the stub server received during register
    //
    // Production scenario this pins: operator debugging a failed-to-poll
    // tentacle. Documented recipe:
    //
    //   sudo squid-tentacle show-thumbprint
    //   → look up that string in server's trust list / machine table
    //   → fix discrepancy
    //
    // The contract operators rely on: <code>show-thumbprint</code>'s
    // stdout EXACTLY MATCHES the thumbprint the binary sent in its own
    // register payload. If a regression makes the two diverge:
    //   - Operator fixes the wrong end of the mismatch
    //   - Or the binary's certificate manager loaded a DIFFERENT cert
    //     during register vs during show-thumbprint (file-mode,
    //     instance-resolution drift)
    //   - Operator sees their fix not take effect, files bugs claiming
    //     "trust list update is broken" when it's actually a local
    //     identity issue
    //
    // Catch-bait: TentacleCertificateManager.LoadOrCreateCertificate has
    // create-or-load semantics. If LoadOrCreateCertificate during register
    // CREATES a new cert (file write succeeds) but during show-thumbprint
    // CREATES ANOTHER cert (because it can't find / read the first), the
    // two thumbprints would differ silently. The cert-path resolution
    // depends on TentacleSettings.CertsPath which gets composed via
    // InstanceSelector.ResolveCertsPath — a regression in that path
    // composition lands here.
    //
    // Test mechanism:
    //   1. Register against stub (Default instance, --listening-port,
    //      --name with GUID-suffixed unique value)
    //   2. Read the stub's recorded register body — it contains the
    //      agent's TentacleThumbprint
    //   3. Run `show-thumbprint`
    //   4. Assert: stdout (single thumbprint line) === thumbprint
    //      extracted from stub's recorded register body
    //
    // Cleanup: rm Default config + cert dir + instances.json.
    //
    // Tier: 🟢 H. Real binary + real /etc/squid-tentacle/ + real cert
    // generation + real stub HTTP exchange.
    //
    // Expected runtime: ~3-5s (register ~1s + show-thumbprint <1s +
    // assertions).
    // ========================================================================

    [Fact]
    public void D1h_ShowThumbprintAfterRegister_MatchesStubReceivedThumbprint()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        using var ctx = new DiagnosticTestContext();

        // ── Step 1: register against stub ─────────────────────────────────
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-D1-SHOW-THUMBPRINT",
            "--name", ctx.MachineName,
            "--role", "diagnostic-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.ListeningPort.ToString(CultureInfo.InvariantCulture));

        regExit.ShouldBe(0,
            customMessage: $"D1h precondition: register MUST succeed before show-thumbprint can be exercised. " +
                          $"Got exit {regExit}.\noutput:\n{regOutput}");

        // ── Step 2: extract agent thumbprint from stub's recorded body ────
        // The register payload (sent by TentacleRegistrationClient /
        // TentacleListeningRegistrar) includes a "tentacleThumbprint"
        // field with the agent's local cert thumbprint. The stub stores
        // each request body verbatim.
        ctx.Stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"stub MUST have received exactly 1 register request. Got {ctx.Stub.ReceivedRegistrations.Count}. " +
                          "If 0: register's REST POST didn't fire (--server flag handling broke). If >1: a retry loop regression " +
                          "(C1.u1 contract broken).");

        var registerBody = ctx.Stub.ReceivedRegistrations[0].Body;
        var stubThumbprint = ExtractThumbprintFromRegisterBody(registerBody);

        stubThumbprint.ShouldNotBeNullOrEmpty(
            customMessage: $"register body MUST contain a tentacleThumbprint field. Without it, server has no way to add the agent " +
                          $"to its trust list. Body:\n{registerBody}");

        // Sanity: thumbprint format is 40 uppercase hex chars (SHA-1).
        stubThumbprint.Length.ShouldBe(40,
            customMessage: $"thumbprint MUST be 40 hex chars (SHA-1). Got '{stubThumbprint}' ({stubThumbprint.Length} chars).");

        // ── Step 3: run show-thumbprint ────────────────────────────────────
        var (showExit, showOutput) = ctx.Binary.SudoRun("show-thumbprint");

        showExit.ShouldBe(0,
            customMessage: $"`show-thumbprint` MUST exit 0 after register. Got exit {showExit}.\noutput:\n{showOutput}");

        // show-thumbprint's stdout is JUST the thumbprint (Console.WriteLine
        // single line). Trim is required because Run wraps stderr appended
        // with NewLine.
        var showThumbprint = showOutput.Trim();

        // ── Step 4: round-trip assertion ──────────────────────────────────
        // The PIN: show-thumbprint's output MATCHES the thumbprint the
        // binary itself sent to the server during register. Case-insensitive
        // because thumbprint comparison is hex (X.509 thumbprint formats
        // sometimes vary on case across .NET versions).
        showThumbprint.ShouldBe(stubThumbprint,
            customMessage: $"show-thumbprint stdout MUST equal the thumbprint sent in the register payload. " +
                          $"\n\nshow-thumbprint output: '{showThumbprint}'\n\nregister body thumbprint: '{stubThumbprint}'\n\n" +
                          $"If they differ: TentacleCertificateManager.LoadOrCreateCertificate generated different certs " +
                          $"during register vs show-thumbprint (cert path resolution drift, OR a race where the cert file " +
                          $"got recreated). Operators' debugging recipe (look up show-thumbprint output in server's trust " +
                          $"list) is broken — the binary tells them one thumbprint, the server saw a different one.");

        ctx.MarkClean();
    }

    // ========================================================================
    // D2.h-Linux — `list-instances` after creating Alpha + Beta shows BOTH
    //
    // Production scenario this pins: operator on a multi-instance host runs
    // `list-instances` to verify their setup ("which agents are on this
    // box?"). The contract: every instance ever persisted via
    // <c>create-instance</c> appears in the output until it's explicitly
    // removed via <c>delete-instance</c> or <c>service uninstall --purge</c>.
    //
    // Without this pin, regressions ship silently:
    //   - InstanceRegistry.List filters wrongly → operator sees one
    //     instance but actually two are on disk
    //   - List output format regression → operator's tooling that parses
    //     the table breaks (e.g. "NAME" header missing breaks awk scripts)
    //   - Empty-list path triggered when state IS present → operator
    //     thinks they have no instances and re-creates, getting a
    //     "Instance Alpha already exists" error (but list-instances
    //     swore it didn't!)
    //
    // Test mechanism:
    //   1. create-instance Alpha (GUID-suffixed unique name)
    //   2. create-instance Beta (GUID-suffixed)
    //   3. Run `list-instances`
    //   4. Assert exit 0
    //   5. Assert stdout contains Alpha's name
    //   6. Assert stdout contains Beta's name
    //   7. Assert stdout contains the "NAME" header (output-format pin —
    //      operators' parsers depend on column header literals)
    //
    // Cleanup: rm both instance dirs + wipe instances.json.
    //
    // Tier: 🟢 H. Real binary + real instances.json round-trip.
    //
    // Expected runtime: ~3-5s (2× create-instance + 1× list-instances +
    // assertions, no register/service install).
    // ========================================================================

    [Fact]
    public void D2h_ListInstancesAfterCreateAlphaAndBeta_ShowsBothEntries()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        using var ctx = new DiagnosticTestContext();

        // GUID-suffixed names per Rule 12.2; reuse the diagnostic ctx's
        // built-in cleanup wiring.
        var alpha = $"diag-alpha-{Guid.NewGuid():N}";
        var beta = $"diag-beta-{Guid.NewGuid():N}";
        ctx.RegisterInstanceForCleanup(alpha);
        ctx.RegisterInstanceForCleanup(beta);

        // ── Setup ──────────────────────────────────────────────────────────
        var (alphaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", alpha);
        alphaCreateExit.ShouldBe(0, "D2h precondition: create-instance Alpha must succeed");

        var (betaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", beta);
        betaCreateExit.ShouldBe(0, "D2h precondition: create-instance Beta must succeed");

        // ── Action: run list-instances ────────────────────────────────────
        var (listExit, listOutput) = ctx.Binary.SudoRun("list-instances");

        listExit.ShouldBe(0,
            customMessage: $"`list-instances` MUST exit 0 even when instances exist. Got exit {listExit}.\noutput:\n{listOutput}");

        // ── Assertions ────────────────────────────────────────────────────
        // Both instance names appear in the output (the substring match
        // is case-sensitive; create-instance preserves casing per the
        // existing instance-name conventions).
        listOutput.ShouldContain(alpha,
            customMessage: $"list-instances output MUST contain '{alpha}' after create-instance. " +
                          "If absent: InstanceRegistry.List filtered it out OR create-instance's Add silently no-op'd. " +
                          $"\noutput:\n{listOutput}");

        listOutput.ShouldContain(beta,
            customMessage: $"list-instances output MUST contain '{beta}' after create-instance. " +
                          $"If absent: same as Alpha case for the second create-instance.");

        // Output-format contract: the "NAME" column header is part of the
        // documented table format that operator scripts (awk / grep) tail.
        // Pin so a future PR that "tidies up" the output (e.g. plain bullet
        // list) breaks here, not silently downstream in operator scripts.
        listOutput.ShouldContain("NAME",
            customMessage: "list-instances output MUST contain 'NAME' header — operators' grep/awk pipelines target this literal. " +
                          "If absent: output format regressed (e.g. table header dropped). Verify intentional, then update test.");

        listOutput.ShouldContain("CONFIG",
            customMessage: "list-instances output MUST contain 'CONFIG' header. If absent: output format regression " +
                          "(possibly along with NAME — both are in the same `Console.WriteLine($\"  {NAME}  CONFIG\")`).");

        // Reverse-pin: the empty-state message MUST NOT appear when state
        // is present (catches a regression where the if/else branch flipped).
        listOutput.ShouldNotContain("No instances registered",
            customMessage: "list-instances output MUST NOT contain 'No instances registered' message when instances exist. " +
                          "If present: empty-state branch is firing despite state on disk — InstanceRegistry.List returned " +
                          "0 elements while instances.json has entries. Likely a path-resolution regression " +
                          "(registry reading from a different file than create-instance writes to).");

        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the <c>tentacleThumbprint</c> field from a register-request
    /// JSON body. Uses regex rather than full JSON parsing to keep test
    /// infrastructure minimal — the field is documented to be a flat
    /// 40-char hex string at the top level of the request payload.
    ///
    /// <para>Returns the thumbprint string, or null if not found (which
    /// is itself an assertable failure — register without a thumbprint
    /// is a regression).</para>
    /// </summary>
    private static string ExtractThumbprintFromRegisterBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;

        // The field name is just "thumbprint" — see
        // TentacleListeningRegistrar.cs line 180:
        //   ["thumbprint"] = identity.Thumbprint,
        // (case-insensitive match to be robust against a serializer
        // settings change that PascalCases keys). The value is a
        // 40-char hex string (SHA-1) wrapped in quotes.
        var match = Regex.Match(body,
            "\"thumbprint\"\\s*:\\s*\"([0-9A-Fa-f]{40})\"",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Per-test context for diagnostic-command scenarios: owns binary
    /// fixture + stub + GUID-suffixed unique identifiers + tracks
    /// instance names that need cleanup. Defensive Dispose handles
    /// failure paths without leaking state into subsequent tests.
    /// </summary>
    private sealed class DiagnosticTestContext : IDisposable
    {
        private bool _clean;
        private readonly List<string> _instanceNamesToCleanup = new();

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public LinuxStubSquidServer Stub { get; } = LinuxStubSquidServer.Start();

        public string MachineName { get; } = $"diag-host-{Guid.NewGuid():N}";

        // Distinct port from B3h's 51933, B4h's 51933, G1h's 51934/51935
        // — diagnostic-command tests don't actually start the agent
        // (just register + show-thumbprint / list-instances), but the
        // listening port is recorded in the persisted config so a
        // distinct port keeps the cross-test debug trail clean.
        public int ListeningPort { get; } = 51940;

        public DiagnosticTestContext()
        {
            // Pre-create /etc/squid-tentacle/instances/ to mimic post-install
            // state (matches B3h / C1h / G1h precondition pattern).
            TrySudo("mkdir", "-p", "/etc/squid-tentacle/instances");
        }

        public void MarkClean() => _clean = true;

        /// <summary>
        /// Records an instance name to clean up in Dispose. Tests that
        /// create instances directly via the binary CLI should call this
        /// immediately after each successful create-instance so failure
        /// paths leave behind the GUID-suffixed (= safe-to-delete) state.
        /// </summary>
        public void RegisterInstanceForCleanup(string instanceName) => _instanceNamesToCleanup.Add(instanceName);

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[DiagnosticTestContext] Dispose without MarkClean — diagnostic test failed before completion.");

            // Default-instance cleanup (D1h registers without --instance →
            // persists Default.config.json + Default/).
            TrySudo("rm", "-f", "/etc/squid-tentacle/instances/Default.config.json");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default");

            // Per-test instance cleanup (D2h's GUID-suffixed Alpha/Beta).
            foreach (var name in _instanceNamesToCleanup)
            {
                TrySudo("rm", "-f", $"/etc/squid-tentacle/instances/{name}.config.json");
                TrySudo("rm", "-rf", $"/etc/squid-tentacle/instances/{name}");
            }

            // instances.json: rm so the next test's create-instance starts
            // from a clean registry.
            TrySudo("rm", "-f", "/etc/squid-tentacle/instances.json");

            // CRITICAL host-state hygiene: the /etc/squid-tentacle/ tree
            // was created by THIS test (mkdir -p in the constructor).
            // Subsequent tests like A2u1
            // ('install fails → /etc/squid-tentacle MUST NOT exist') expect
            // a clean state. Use `rmdir --ignore-fail-on-non-empty` so
            // we only delete if empty — defensive against the case
            // where another test legitimately stages files there.
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle/instances");
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle");

            try { Stub.Dispose(); } catch { /* best-effort */ }
        }

        private static void TrySudo(string cmd, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sudo",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(cmd);
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }
    }
}
