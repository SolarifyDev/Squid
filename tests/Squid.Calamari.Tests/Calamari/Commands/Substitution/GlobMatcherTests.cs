using System.IO;
using Shouldly;
using Squid.Calamari.Commands.Substitution;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Substitution;

/// <summary>
/// G1.1 — glob-to-file-enumeration tests. Operator-facing patterns the IIS
/// handler advertises (per <c>Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles</c>
/// preamble in the deploy script) are newline-separated globs that operators
/// already use with Octopus, e.g. <c>web.config</c>, <c>**/*.config</c>,
/// <c>appsettings*.json</c>. Pin the matcher behaviour for each shape.
/// </summary>
public sealed class GlobMatcherTests : IDisposable
{
    private readonly string _root;

    public GlobMatcherTests()
    {
        // Use a unique temp dir per test class instance — xUnit creates a new
        // instance per test so this is per-test isolated.
        _root = Path.Combine(Path.GetTempPath(), $"glob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Expand_LiteralFilename_ReturnsExactFile()
    {
        File.WriteAllText(Path.Combine(_root, "web.config"), "");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "");

        var matches = GlobMatcher.Expand(_root, "web.config").ToList();

        matches.Count.ShouldBe(1);
        matches[0].ShouldEndWith("web.config");
    }

    [Fact]
    public void Expand_AsteriskInName_MatchesAllInSegment()
    {
        File.WriteAllText(Path.Combine(_root, "web.config"), "");
        File.WriteAllText(Path.Combine(_root, "app.config"), "");
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "");

        var matches = GlobMatcher.Expand(_root, "*.config").Select(Path.GetFileName).OrderBy(x => x).ToList();

        matches.ShouldBe(new[] { "app.config", "web.config" });
    }

    [Fact]
    public void Expand_DoubleAsterisk_RecursesAllSubdirs()
    {
        File.WriteAllText(Path.Combine(_root, "top.config"), "");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "nested.config"), "");
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deep"));
        File.WriteAllText(Path.Combine(_root, "sub", "deep", "buried.config"), "");

        var matches = GlobMatcher.Expand(_root, "**/*.config").Select(p => Path.GetFileName(p)).OrderBy(x => x).ToList();

        matches.ShouldBe(new[] { "buried.config", "nested.config", "top.config" });
    }

    [Fact]
    public void Expand_NoMatches_ReturnsEmpty_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_root, "web.config"), "");

        var matches = GlobMatcher.Expand(_root, "*.notreal").ToList();

        matches.ShouldBeEmpty(
            customMessage: "Operator-typo globs MUST yield empty enumeration, not exception — the step logs a warning and continues.");
    }

    [Fact]
    public void Expand_DotIsLiteral_NotRegexAnyChar()
    {
        // Defence against regex injection: `web.config` should match ONLY
        // `web.config`, not `webXconfig` or `webconfig`.
        File.WriteAllText(Path.Combine(_root, "web.config"), "");
        File.WriteAllText(Path.Combine(_root, "webXconfig"), "");

        var matches = GlobMatcher.Expand(_root, "web.config").Select(Path.GetFileName).ToList();

        matches.Count.ShouldBe(1);
        matches[0].ShouldBe("web.config",
            customMessage: "Dot in glob MUST be literal, not regex any-char — otherwise `web.config` would also match `webXconfig`.");
    }

    [Fact]
    public void Expand_RootDirDoesNotExist_ReturnsEmpty_DoesNotThrow()
    {
        var matches = GlobMatcher.Expand(Path.Combine(_root, "missing"), "*.config").ToList();

        matches.ShouldBeEmpty(
            customMessage: "Pre-extract scenario: working dir not yet created → step gracefully skips, doesn't crash the pipeline.");
    }

    [Fact]
    public void Expand_EmptyPattern_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_root, "web.config"), "");

        var matches = GlobMatcher.Expand(_root, "").ToList();

        matches.ShouldBeEmpty();
    }

    [Fact]
    public void Expand_PathTraversalAttempt_BoundedToRoot()
    {
        // Security: `../../etc/passwd` MUST NOT escape the working dir.
        // The matcher resolves relative to the root + filters anything outside.
        // Even if not paranoid, no operator config would legitimately need
        // this — fail-safe to reject.
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "inside.config"), "");

        var matches = GlobMatcher.Expand(_root, "../*.config").ToList();

        matches.ShouldBeEmpty(
            customMessage: "Path-traversal globs MUST yield zero matches — substitution is sandboxed to the working dir to prevent malicious package content from rewriting host files.");
    }

    [Fact]
    public void Expand_SymlinkEscapingRoot_Excluded()
    {
        // Attack scenario: a malicious package unpacks a symlink
        // `evil.config -> /etc/passwd` (or any host file). A naive glob
        // `*.config` would match `evil.config`; the rewriter would happily
        // overwrite `/etc/passwd`. On a Tentacle running as root (systemd
        // unit), that's a host compromise.
        //
        // The match-level sandbox MUST detect the link target, resolve to
        // canonical real path, and drop matches whose real path lives
        // outside the working dir.
        //
        // Symlink creation requires elevated privileges on Windows pre-1809;
        // skip there cleanly so cross-OS dev hosts get green tests.
        if (OperatingSystem.IsWindows()) return;

        // Set up a victim file OUTSIDE the working dir.
        var outsideDir = Path.Combine(Path.GetTempPath(), $"glob-victim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var victim = Path.Combine(outsideDir, "secret.config");
        File.WriteAllText(victim, "host-secret-content");

        try
        {
            // Plant a symlink inside the working dir pointing at the victim.
            var attackLink = Path.Combine(_root, "evil.config");
            File.CreateSymbolicLink(attackLink, victim);

            // Also drop a legitimate sibling that SHOULD match — to confirm
            // the sandbox only excludes the escapee, not the entire glob.
            File.WriteAllText(Path.Combine(_root, "legit.config"), "");

            var matches = GlobMatcher.Expand(_root, "*.config").ToList();

            matches.Select(Path.GetFileName).ShouldNotContain("evil.config",
                customMessage: "Symlink pointing outside the working dir MUST be excluded from glob matches. " +
                               "If this fails, a malicious package can rewrite host files via planted symlinks.");
            matches.Select(Path.GetFileName).ShouldContain("legit.config",
                customMessage: "Sibling files NOT pointing outside MUST still match — only the escapee is excluded.");

            // Sanity: the victim file is still intact (the matcher didn't
            // even need to touch it, but assert just so the test reads honestly).
            File.ReadAllText(victim).ShouldBe("host-secret-content");
        }
        finally
        {
            if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void Expand_SymlinkPointingInsideRoot_StillIncluded()
    {
        // Counter-test: not every symlink is hostile. A symlink that resolves
        // to a file INSIDE the working dir is legitimate (operator's package
        // might use them for shared-config patterns). The sandbox MUST NOT
        // be overly aggressive — only excludes escapees.
        if (OperatingSystem.IsWindows()) return;

        var realFile = Path.Combine(_root, "real.config");
        File.WriteAllText(realFile, "real-content");

        var insideLink = Path.Combine(_root, "alias.config");
        File.CreateSymbolicLink(insideLink, realFile);

        var matches = GlobMatcher.Expand(_root, "*.config").ToList();

        matches.Select(Path.GetFileName).ShouldContain("alias.config",
            customMessage: "Symlinks resolving INSIDE the working dir are legitimate and MUST be included.");
        matches.Select(Path.GetFileName).ShouldContain("real.config");
    }
}
