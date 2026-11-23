using System.Diagnostics;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.SelfHeal;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH. Real production classes against real OS filesystems; expectations come from
/// <c>df -P -k</c>, not from the implementation. The "separate filesystem" cases need a mount that is
/// NOT the root filesystem: Linux uses /dev/shm (tmpfs); the "fill the volume" cases need a SMALL volume, so Linux
/// mounts an 8 MB tmpfs through passwordless sudo (GitHub-hosted runners have it) and macOS attaches a small HFS+
/// disk image (<c>hdiutil</c>, no root needed). Where the platform should support a fillable volume (Linux with
/// sudo, macOS, or any CI run) failing to create one FAILS the test instead of silently passing. Windows is
/// skipped (no df).
/// </summary>
[Trait("Category", "TentacleDiskSpaceE2E")]
public class DiskSpaceCheckerRealMountTests
{
    [Fact]
    public void GetDiskSpace_MatchesDf_ForRealPaths()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var path in new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/" })
        {
            var df = Df(path);
            var (_, total) = DiskSpaceChecker.GetDiskSpace(path);

            total.ShouldBe(df.TotalBytes, $"total size for {path} must match `df -P -k {path}` (check manually: df -P -k {path})");
        }
    }

    [Fact]
    public void ResolveVolumeRoot_MatchesDfMountedOn_ForRealPaths()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var path in new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/" })
        {
            var resolved = VolumeResolver.ResolveVolumeRoot(path);
            var dfMount = Df(path).MountedOn;

            AssertSameMount(resolved, dfMount, path);
        }
    }

    [Fact]
    public void GetDiskSpace_PathOnSeparateFilesystem_MeasuresThatFilesystemNotRoot()
    {
        using var volume = SeparateVolume.TryCreate();

        if (volume == null) return;

        var (_, total) = DiskSpaceChecker.GetDiskSpace(volume.Path);
        var (_, rootTotal) = LegacyGetPathRootMeasure(volume.Path);

        total.ShouldBe(Df(volume.Path).TotalBytes);
        total.ShouldNotBe(rootTotal, "sanity: the separate filesystem must differ from '/' or this test proves nothing");
    }

    [Fact]
    public void GetDiskSpace_NonExistentChildOfSeparateFilesystem_WalksUpToIt()
    {
        using var volume = SeparateVolume.TryCreate();

        if (volume == null) return;

        var missing = System.IO.Path.Combine(volume.Path, "not", "created", "yet");

        DiskSpaceChecker.GetDiskSpace(missing).Total.ShouldBe(Df(volume.Path).TotalBytes);
    }

    [Fact]
    public void GetDiskSpace_SymlinkIntoSeparateFilesystem_MeasuresTheTarget()
    {
        using var volume = SeparateVolume.TryCreate();

        if (volume == null) return;

        var link = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squid-link-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, volume.Path);

        try
        {
            DiskSpaceChecker.GetDiskSpace(link).Total.ShouldBe(Df(volume.Path).TotalBytes);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void EnsureDiskHasEnoughFreeSpace_FullSeparateFilesystem_Throws_EvenWhenRootHasSpace()
    {
        using var volume = SeparateVolume.CreateFillable();

        if (volume == null) return;

        volume.FillTo(freeFraction: 0.02);

        // Calls the ungated check so the process-global Enabled flag (relied on by parallel script tests) is untouched.
        var message = Should.Throw<IOException>(() => DiskSpaceChecker.EnsureEnoughFreeSpace(volume.Path)).Message;

        // The reported capacity must be the volume's own (not '/'), proving which filesystem was measured.
        message.ShouldContain(volume.Path);
        message.ShouldContain(DiskSpaceChecker.FormatBytes(Df(volume.Path).TotalBytes));
    }

    [Fact]
    public void SelfHealDefaultProbe_MeasuresTheWorkspacesFilesystem()
    {
        using var volume = SeparateVolume.TryCreate();

        if (volume == null) return;

        var pressure = DiskPressureHealAction.DefaultDiskProbe(volume.Path);

        pressure.TotalBytes.ShouldBe(Df(volume.Path).TotalBytes);
    }

    [Fact]
    public void WorkspaceUsage_OnSeparateFilesystem_ReportsThatFilesystemsFreeSpace()
    {
        using var volume = SeparateVolume.CreateFillable();

        if (volume == null) return;

        volume.FillTo(freeFraction: 0.02);

        var usage = DiskSpaceChecker.GetWorkspaceUsage(volume.Path);

        usage.TotalBytes.ShouldBe(Df(volume.Path).TotalBytes);
        usage.IsLowSpace.ShouldBeTrue();
    }

    [Fact]
    public void ProductionCode_MeasuresDiskOnlyThroughDiskSpaceChecker()
    {
        // Wiring guard: a caller that builds its own DriveInfo / GetPathRoot bypasses the volume
        // resolution and silently reintroduces the "always measures /" bug.
        var allowed = new[] { "DiskSpaceChecker.cs", "VolumeResolver.cs" };
        var offenders = Directory.EnumerateFiles(System.IO.Path.Combine(RepoRoot(), "src", "Squid.Tentacle"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !allowed.Contains(System.IO.Path.GetFileName(f)) && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("new DriveInfo(") || File.ReadAllText(f).Contains("GetPathRoot("))
            .Select(System.IO.Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty($"measure disk through DiskSpaceChecker.GetDiskSpace — offenders: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("ScriptExecution/LocalScriptService.cs")]
    [InlineData("ScriptExecution/ScriptPodService.cs")]
    [InlineData("ScriptExecution/ScriptPodService.Workspace.cs")]
    [InlineData("Flavors/KubernetesAgent/KubernetesAgentFlavor.cs")]
    [InlineData("Kubernetes/KubernetesPodMonitor.cs")]
    [InlineData("SelfHeal/DiskPressureHealAction.cs")]
    public void EveryDiskCaller_StillGoesThroughDiskSpaceChecker(string relativePath)
    {
        var source = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Squid.Tentacle", relativePath));

        source.ShouldContain("DiskSpaceChecker.");
    }

    private static void AssertSameMount(string resolved, string dfMount, string path)
    {
        // macOS firmlinks: df reports the Data volume for /Users, /private/var, ... while the path text
        // only matches "/". Both are slices of one APFS container and report identical sizes, so the
        // measured numbers (asserted in GetDiskSpace_MatchesDf_ForRealPaths) are unaffected.
        var firmlinkAlias = OperatingSystem.IsMacOS() && dfMount == "/System/Volumes/Data" && resolved == "/";

        (resolved == dfMount || firmlinkAlias).ShouldBeTrue($"{path}: resolved '{resolved}' but df says '{dfMount}'");
    }

    private static (long Available, long Total) LegacyGetPathRootMeasure(string path)
    {
        var drive = new DriveInfo(System.IO.Path.GetPathRoot(path) ?? path);

        return (drive.AvailableFreeSpace, drive.TotalSize);
    }

    private static DfRow Df(string path) => DfRow.Read(path);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        // .git is a FILE in a linked worktree, so test both.
        while (dir != null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")) && !File.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }

    private sealed record DfRow(long TotalBytes, long AvailableBytes, string MountedOn)
    {
        public static DfRow Read(string path)
        {
            var output = Run("df", $"-P -k \"{path}\"");
            var tokens = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return new DfRow(long.Parse(tokens[1]) * 1024, long.Parse(tokens[3]) * 1024, string.Join(' ', tokens.Skip(5)));
        }
    }

    private static int RunExit(string file, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true });

            process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string Run(string file, string args)
    {
        var info = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info);
        var output = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        return output;
    }

    /// <summary>A real filesystem that is not '/': /dev/shm on Linux, a throwaway HFS+ image on macOS.</summary>
    private sealed class SeparateVolume : IDisposable
    {
        private readonly string _imagePath;
        private readonly string _dir;

        private SeparateVolume(string path, string dir, string imagePath)
        {
            Path = path;
            _dir = dir;
            _imagePath = imagePath;
        }

        public string Path { get; }

        public bool CanFill => _imagePath != null || _mountedTmpfs;

        private bool _mountedTmpfs;

        public static SeparateVolume TryCreate()
        {
            if (OperatingSystem.IsLinux()) return TryCreateLinux();

            return OperatingSystem.IsMacOS() ? TryCreateMac() : null;
        }

        /// <summary>
        /// A small volume the test can fill. Returns null ONLY where it is legitimately unsupported (Windows, or a
        /// developer Linux box without passwordless sudo outside CI); anywhere the platform should support it a
        /// creation failure fails the test, so these cases can no longer pass by silently doing nothing.
        /// </summary>
        public static SeparateVolume CreateFillable()
        {
            if (OperatingSystem.IsWindows()) return null;

            var volume = OperatingSystem.IsLinux() ? TryCreateLinuxTmpfs() : TryCreateMac();

            if (volume is { CanFill: true }) return volume;

            volume?.Dispose();

            if (ShouldSupportFillableVolume())
                Assert.Fail("Could not create a small fillable volume on a platform that should support one. " +
                            (OperatingSystem.IsLinux() ? "Check `sudo -n mount -t tmpfs -o size=8m tmpfs <dir>` works for this user." : "Check `hdiutil create/attach` works."));

            Console.WriteLine("[DiskSpaceCheckerRealMountTests] no fillable volume available on this developer host (no passwordless sudo) - case skipped.");

            return null;
        }

        private static bool ShouldSupportFillableVolume()
        {
            if (OperatingSystem.IsMacOS()) return true;

            var onCi = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

            return onCi || RunExit("sudo", "-n true") == 0;
        }

        private static SeparateVolume TryCreateLinuxTmpfs()
        {
            if (RunExit("sudo", "-n true") != 0) return null;

            var dir = Directory.CreateTempSubdirectory("squid-tmpfs-").FullName;

            if (RunExit("sudo", $"-n mount -t tmpfs -o size=8m,mode=1777 tmpfs \"{dir}\"") != 0 || !DiffersFromRoot(dir))
            {
                RunExit("sudo", $"-n umount \"{dir}\"");
                Directory.Delete(dir, true);

                return null;
            }

            return new SeparateVolume(dir, dir, null) { _mountedTmpfs = true };
        }

        private static SeparateVolume TryCreateLinux()
        {
            if (!Directory.Exists("/dev/shm")) return null;

            var dir = System.IO.Path.Combine("/dev/shm", $"squid-disk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            return DiffersFromRoot(dir) ? new SeparateVolume(dir, dir, null) : null;
        }

        private static SeparateVolume TryCreateMac()
        {
            var dir = Directory.CreateTempSubdirectory("squid-vol-").FullName;
            var image = System.IO.Path.Combine(dir, "vol.dmg");
            var mount = System.IO.Path.Combine(dir, "mnt");

            Run("hdiutil", $"create -size 8m -fs HFS+ -volname squidtest -ov \"{image}\"");
            Run("hdiutil", $"attach -nobrowse -mountpoint \"{mount}\" \"{image}\"");

            if (Directory.Exists(mount) && DiffersFromRoot(mount)) return new SeparateVolume(mount, dir, image);

            Run("hdiutil", $"detach -force \"{mount}\"");
            Directory.Delete(dir, true);

            return null;
        }

        private static bool DiffersFromRoot(string path) => LegacyGetPathRootMeasure(path).Total != DfRow.Read(path).TotalBytes;

        public void FillTo(double freeFraction)
        {
            var total = DfRow.Read(Path).TotalBytes;
            var toWrite = DfRow.Read(Path).AvailableBytes - (long)(total * freeFraction);

            File.WriteAllBytes(System.IO.Path.Combine(Path, "filler.bin"), new byte[Math.Max(toWrite, 0)]);
        }

        public void Dispose()
        {
            if (_mountedTmpfs)
            {
                RunExit("sudo", $"-n umount \"{Path}\"");
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            }
            else if (_imagePath != null)
            {
                Run("hdiutil", $"detach -force \"{Path}\"");
                Directory.Delete(_dir, true);
            }
            else if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
    }
}
