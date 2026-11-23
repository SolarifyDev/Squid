using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class VolumeResolverTests
{
    private const StringComparison Ordinal = StringComparison.Ordinal;
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    [Theory]
    [InlineData("/var/lib/squid/work/ticket-1", "/|/var|/var/lib/squid", "/var/lib/squid")]   // deepest nested mount wins
    [InlineData("/var/log/x", "/|/var|/var/lib/squid", "/var")]                              // sibling of the deepest falls back to /var
    [InlineData("/home/u", "/|/var|/var/lib/squid", "/")]                                    // unrelated path -> root
    [InlineData("/database/x", "/|/data", "/")]                                              // component boundary: /data must not match /database
    [InlineData("/data/x", "/|/data", "/data")]
    [InlineData("/data", "/|/data", "/data")]                                                // path == mount root
    [InlineData("/data/", "/|/data", "/data")]                                               // trailing slash on path
    [InlineData("/data/x", "/|/data/", "/data/")]                                            // trailing slash on mount root
    [InlineData("/var/lib/squid", "/var/lib/squid|/var|/", "/var/lib/squid")]                // input order must not matter
    [InlineData("/x", "/data|/var", null)]                                                   // no match -> null (caller falls back)
    [InlineData("/", "/|/var", "/")]
    public void PickMountRoot_Unix(string path, string mounts, string expected)
    {
        VolumeResolver.PickMountRoot(path, mounts.Split('|'), '/', Ordinal).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\squid\work", @"C:\|D:\", @"C:\")]
    [InlineData(@"d:\squid\work", @"C:\|D:\", @"D:\")]                                       // drive letters are case-insensitive
    [InlineData(@"C:\mnt\vol\x", @"C:\|C:\mnt\vol", @"C:\mnt\vol")]                          // mounted folder beats drive root
    [InlineData(@"C:\mnt\volume2\x", @"C:\|C:\mnt\vol", @"C:\")]                             // component boundary on Windows
    [InlineData(@"E:\x", @"C:\|D:\", null)]
    public void PickMountRoot_Windows(string path, string mounts, string expected)
    {
        VolumeResolver.PickMountRoot(path, mounts.Split('|'), '\\', IgnoreCase).ShouldBe(expected);
    }

    [Fact]
    public void ResolveVolumeRoot_NonExistentPath_WalksUpToExistingAncestor()
    {
        var existing = VolumeResolver.ResolveVolumeRoot(Path.GetTempPath());
        var missing = VolumeResolver.ResolveVolumeRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a", "b"));

        existing.ShouldNotBeNull();
        missing.ShouldBe(existing);
    }

    [Fact]
    public void PickReadyMountRoot_ProbesOnlyMatchingCandidates_BestFirst_NeverUnrelatedMounts()
    {
        var probed = new List<string>();
        bool Probe(string root)
        {
            probed.Add(root);
            return root != "/hung-nfs" ? true : throw new InvalidOperationException("an unrelated mount must never be probed");
        }

        var picked = VolumeResolver.PickReadyMountRoot("/var/lib/squid/work", new[] { "/", "/hung-nfs", "/var", "/var/lib/squid", "/mnt/other" }, '/', Ordinal, Probe);

        picked.ShouldBe("/var/lib/squid");
        probed.ShouldBe(new[] { "/var/lib/squid" });
    }

    [Fact]
    public void PickReadyMountRoot_BestCandidateNotReady_FallsBackToNextBest()
    {
        var probed = new List<string>();

        var picked = VolumeResolver.PickReadyMountRoot("/var/lib/squid/work", new[] { "/", "/hung-nfs", "/var", "/var/lib/squid" }, '/', Ordinal, r => { probed.Add(r); return r != "/var/lib/squid"; });

        picked.ShouldBe("/var");
        probed.ShouldBe(new[] { "/var/lib/squid", "/var" });
    }

    [Fact]
    public void PickReadyMountRoot_NothingReady_ReturnsNull()
    {
        VolumeResolver.PickReadyMountRoot("/a/b", new[] { "/", "/a" }, '/', Ordinal, _ => false).ShouldBeNull();
    }
}
