using System.Diagnostics;
using System.Linq;
using System.Text;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Inspectors;
using Squid.Message.Models.Publishing;
using Squid.UnitTests.Services.Packages.BuiltIn;

namespace Squid.UnitTests.Services.Publishing;

/// <summary>Hostile and oversized input: nesting that would overflow a recursive reader, huge values, archives that hide their markers, headers that lie.</summary>
public class PackageInspectionHardeningTests
{
    // === squid.yaml: never an object graph, never a stack overflow ===

    [Theory]
    [InlineData(10_000)]
    [InlineData(200_000)]
    public async Task SquidYaml_DeepFlowSequence_IsAnErrorFact_AndReturnsQuickly(int depth)
    {
        var yaml = "recipe: " + new string('[', depth) + new string(']', depth);

        var (facts, elapsed) = await Timed(yaml);

        facts.Get(PackageFactKeys.SquidError).ShouldContain("deeper");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SquidYaml_DeepFlowMapping_IsAnErrorFact_AndReturnsQuickly()
    {
        var yaml = string.Concat(Enumerable.Repeat("{a: ", 50_000)) + "1" + new string('}', 50_000);

        var (facts, elapsed) = await Timed(yaml);

        facts.Get(PackageFactKeys.SquidError).ShouldContain("deeper");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SquidYaml_DeepBlockMapping_IsAnErrorFact()
    {
        var yaml = string.Concat(Enumerable.Range(0, 600).Select(i => new string(' ', i) + "k:\n")) + new string(' ', 600) + "v: 1";

        var (facts, _) = await Timed(yaml);

        facts.Get(PackageFactKeys.SquidError).ShouldContain("deeper");
    }

    [Fact]
    public async Task SquidYaml_DeepBlockSequence_IsAnErrorFact()
    {
        var yaml = string.Concat(Enumerable.Range(0, 600).Select(i => new string(' ', i) + "-\n")) + new string(' ', 600) + "- 1";

        (await Timed(yaml)).Facts.Get(PackageFactKeys.SquidError).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SquidYaml_TooManyEvents_IsAnErrorFact()
    {
        var yaml = string.Concat(Enumerable.Range(0, SquidYamlInspector.MaxEvents).Select(i => $"k{i}: v\n"));

        (await Timed(yaml)).Facts.Get(PackageFactKeys.SquidError).ShouldContain("events");
    }

    [Fact]
    public async Task SquidYaml_NestingWithinTheLimit_StillReadsTheWantedScalars()
    {
        var yaml = "extra:\n  deep: {a: {b: [1, 2, {c: d}]}}\nrecipe: Docker\nrun:\n  port: 8080\n  command: node a.js\nhealth: {path: /up}\n";

        var facts = (await Timed(yaml)).Facts;

        facts.Get(PackageFactKeys.SquidRecipe).ShouldBe("Docker");
        facts.Get(PackageFactKeys.SquidRunPort).ShouldBe("8080");
        facts.Get(PackageFactKeys.SquidRunCommand).ShouldBe("node a.js");
        facts.Get(PackageFactKeys.SquidHealthPath).ShouldBe("/up");
        facts.Values.ContainsKey(PackageFactKeys.SquidError).ShouldBeFalse();
    }

    [Theory]
    [InlineData("recipe:\n  - a\n  - b\n")]
    [InlineData("recipe: {x: y}\n")]
    [InlineData("list:\n  - recipe: Docker\n")]
    [InlineData("run: [port, 8080]\n")]
    public async Task SquidYaml_WantedNamesInTheWrongShape_AreIgnored(string yaml)
    {
        var facts = (await Timed(yaml)).Facts;

        facts.Values.Keys.Where(k => k.StartsWith("squid.")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("just a scalar")]
    [InlineData("- a\n- b")]
    [InlineData("*alias")]
    public async Task SquidYaml_ThatIsNotAMapping_IsAnErrorFact(string yaml)
    {
        (await Timed(yaml)).Facts.Get(PackageFactKeys.SquidError).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SquidYaml_KeyAfterAnAliasedValue_IsNotMisread()
    {
        var facts = (await Timed("base: &b value\ncopy: *b\nrecipe: Docker\n")).Facts;

        facts.Get(PackageFactKeys.SquidRecipe).ShouldBe("Docker");
    }

    private static async Task<(PackageFacts Facts, TimeSpan Elapsed)> Timed(string yaml)
    {
        var clock = Stopwatch.StartNew();
        var facts = await Inspect.ZipAsync(("squid.yaml", yaml));

        return (facts, clock.Elapsed);
    }

    // === value and size caps ===

    [Fact]
    public async Task HugeValues_AreCutWithAMarker_AndTheStoredFactsStaySmall()
    {
        var yaml = "recipe: " + new string('r', 200_000) + "\nrun:\n  command: " + new string('c', 600_000) + "\nhealth:\n  path: " + new string('h', 1000) + "\n";
        var package = """{"engines":{"node":"%"}}""".Replace("%", new string('9', 5000));

        var facts = await Inspect.ZipAsync(("squid.yaml", yaml), ("package.json", package));

        foreach (var key in new[] { PackageFactKeys.SquidRecipe, PackageFactKeys.SquidRunCommand, PackageFactKeys.SquidHealthPath, PackageFactKeys.NodeEngines })
        {
            facts.Get(key).Length.ShouldBe(PackageInspectionContext.MaxValueLength, key);
            facts.Get(key).ShouldEndWith(PackageInspectionContext.TruncationMarker);
        }

        PackageFactsJson.Serialize(facts).Length.ShouldBeLessThan(4096);
    }

    [Fact]
    public async Task ValuesAtTheCap_AreKeptWhole()
    {
        var command = new string('c', PackageInspectionContext.MaxValueLength);

        (await Inspect.ZipAsync(("squid.yaml", $"run:\n  command: {command}\n"))).Get(PackageFactKeys.SquidRunCommand).ShouldBe(command);
    }

    [Fact]
    public async Task InspectorFailureMessages_AreCapped()
    {
        var facts = await Inspect.RunAsync(TestArchives.Zip(("a", "b")), BuiltInPackageFormat.Zip, new LongMessageInspector());

        facts.Get(PackageFactKeys.InspectorFailedPrefix + nameof(LongMessageInspector)).Length.ShouldBe(PackageInspectionContext.MaxValueLength);
    }

    [Fact]
    public async Task AnInspectorThatRecordsGreatManyFacts_CannotPushTheStoredFactsPastTheCap()
    {
        var facts = await Inspect.RunAsync(TestArchives.Zip(("a", "b")), BuiltInPackageFormat.Zip, new FloodInspector());

        PackageFactsJson.Serialize(facts).Length.ShouldBeLessThanOrEqualTo(PackageInspectionService.MaxFactsJsonLength);
        facts.Is(PackageFactKeys.FactsTruncated).ShouldBeTrue();
        facts.Values.Count.ShouldBeLessThan(200);
    }

    [Fact]
    public async Task FactsUnderTheCap_AreNotMarkedTruncated()
    {
        (await Inspect.ZipAsync(("Dockerfile", "FROM x"))).Values.ContainsKey(PackageFactKeys.FactsTruncated).ShouldBeFalse();
    }

    // === listing correctness past the retained names ===

    private static (string, string)[] Filler(string folder) => Enumerable.Range(0, PackageArchiveScanner.MaxRetainedNames + 1).Select(i => ($"{folder}/m{i}/index.js", "x")).ToArray();

    [Fact]
    public async Task RootMarkers_AfterMoreEntriesThanAreRetained_AreStillFound_AndTheRootIsNotTheFirstFolder()
    {
        var entries = Filler("node_modules").Concat([("package.json", """{"scripts":{"build":"x"}}"""), ("Dockerfile", "FROM x"), ("index.html", "<html/>")]).ToArray();

        foreach (var facts in new[] { await Inspect.ZipAsync(entries), await Inspect.TarGzAsync(entries) })
        {
            facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe(string.Empty);
            facts.Is(PackageFactKeys.ArchiveWrapped).ShouldBeFalse();
            facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
            facts.Is(PackageFactKeys.MarkerIndexHtml).ShouldBeTrue();
            facts.Is(PackageFactKeys.NodeBuildScript).ShouldBeTrue("a file read late in the archive is still read");
            facts.Get(PackageFactKeys.FilesCount).ShouldBe((PackageArchiveScanner.MaxRetainedNames + 4).ToString());
        }
    }

    [Fact]
    public async Task WrappedMarkers_AfterMoreEntriesThanAreRetained_AreFoundInsideTheWrapper()
    {
        var entries = Filler("site").Concat([("site/Dockerfile", "FROM x"), ("site/package.json", """{"scripts":{"start":"x"}}""")]).ToArray();

        foreach (var facts in new[] { await Inspect.ZipAsync(entries), await Inspect.TarGzAsync(entries) })
        {
            facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe("site");
            facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
            facts.Is(PackageFactKeys.NodeStartScript).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AFolderThatMerelyComesFirst_IsNotTheRoot_WhenALaterTopLevelFolderExists()
    {
        var entries = Filler("a").Concat([("b/Dockerfile", "FROM x")]).ToArray();

        var facts = await Inspect.ZipAsync(entries);

        facts.Is(PackageFactKeys.ArchiveWrapped).ShouldBeFalse();
        facts.Values.ContainsKey(PackageFactKeys.MarkerDockerfile).ShouldBeFalse();
    }

    [Fact]
    public async Task AMacResourceForkFlood_DoesNotHideTheRealFiles_OrCountTowardThem()
    {
        var entries = Enumerable.Range(0, PackageArchiveScanner.MaxRetainedNames + 1000).Select(i => ($"__MACOSX/app/._f{i}", "x")).Concat([("app/Dockerfile", "FROM x"), ("app/main.go", "package main")]).ToArray();

        foreach (var facts in new[] { await Inspect.ZipAsync(entries), await Inspect.TarGzAsync(entries) })
        {
            facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe("app");
            facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
            facts.Get(PackageFactKeys.FilesCount).ShouldBe("2");
            facts.Values.ContainsKey(PackageFactKeys.FilesTruncated).ShouldBeFalse();
        }
    }

    // === what the scan retains, and when it stops ===

    private static async Task<PackageArchiveScan> ScanAsync(byte[] archive, BuiltInPackageFormat format, IReadOnlySet<string> reads, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squid-scan-{Guid.NewGuid():N}");

        await System.IO.File.WriteAllBytesAsync(path, archive);

        try
        {
            return await PackageArchiveScanner.ScanAsync(new PackageArchiveTextReader(path, format), reads, reads, ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Scan_RetainsAtMostTheCappedNames_AndOnlyTheFirstFoldersProbedFiles()
    {
        var entries = Enumerable.Range(0, 4000).Select(i => ($"pkg{i}/package.json", "{}")).Concat(Enumerable.Range(0, 3000).Select(i => ($"pkg0/f{i}.txt", "x"))).ToArray();
        var reads = new HashSet<string> { "package.json" };

        foreach (var scan in new[] { await ScanAsync(TestArchives.Zip(entries), BuiltInPackageFormat.Zip, reads), await ScanAsync(TestArchives.TarGz(entries), BuiltInPackageFormat.TarGz, reads) })
        {
            scan.Count.ShouldBe(7000);
            scan.Names.Count.ShouldBe(PackageArchiveScanner.MaxRetainedNames);
            scan.Texts.Keys.ShouldBe(["pkg0/package.json"]); // 4000 folders hold a package.json, only the first can be the root
            scan.Present.ShouldBe(["pkg0/package.json"]);
        }
    }

    [Fact]
    public async Task Scan_StopsWalkingWhenCancelled()
    {
        var entries = Enumerable.Range(0, 3000).Select(i => ($"f{i}.txt", "x")).ToArray();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squid-scan-{Guid.NewGuid():N}");

        await System.IO.File.WriteAllBytesAsync(path, TestArchives.Zip(entries));

        try
        {
            using var cts = new CancellationTokenSource();
            var visited = 0;

            await Should.ThrowAsync<OperationCanceledException>(() => new PackageArchiveTextReader(path, BuiltInPackageFormat.Zip).WalkFilesAsync((_, _, _) => { if (++visited == 10) cts.Cancel(); }, cts.Token));

            visited.ShouldBe(10, "the walk must stop at the next entry, not finish the archive");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inspection_WithACancelledToken_StopsAndThrows(bool zip)
    {
        var entries = Enumerable.Range(0, PackageArchiveScanner.MaxRetainedNames + 10).Select(i => ($"f{i}.txt", "x")).ToArray();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squid-scan-{Guid.NewGuid():N}");

        await System.IO.File.WriteAllBytesAsync(path, zip ? TestArchives.Zip(entries) : TestArchives.TarGz(entries));

        try
        {
            using var cts = new CancellationTokenSource();

            cts.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(() => new PackageInspectionService(Inspect.AllInspectors()).InspectAsync(path, zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz, cts.Token));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // === reading is capped by the stream, not by the header ===

    [Fact]
    public void ReadBoundedText_StopsAtTheCap_WhateverTheStreamHolds()
    {
        using var over = new CountingStream(PackageArchiveTextReader.MaxTextBytes * 50L);
        using var exactly = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(new string('a', PackageArchiveTextReader.MaxTextBytes)));

        PackageArchiveTextReader.ReadBoundedText(over).ShouldBeNull();
        over.BytesRead.ShouldBeLessThan(PackageArchiveTextReader.MaxTextBytes + 16 * 1024, "it must stop reading, not read 50 MB and then discard it");
        PackageArchiveTextReader.ReadBoundedText(exactly).Length.ShouldBe(PackageArchiveTextReader.MaxTextBytes);
    }

    [Fact]
    public void ReadBoundedText_StripsABom_AndLeavesTheStreamOpen()
    {
        using var stream = new System.IO.MemoryStream([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);

        PackageArchiveTextReader.ReadBoundedText(stream).ShouldBe("{}");
        stream.CanRead.ShouldBeTrue("a tar entry's stream must stay usable to skip past the entry");
    }

    [Fact]
    public async Task AnEntryWhoseHeaderUnderstatesItsSize_IsNeverReadInFull()
    {
        var json = """{"scripts":{"build":"x"}}""";
        var zip = FalsifySizes(TestArchives.Zip(("package.json", new string(' ', PackageArchiveTextReader.MaxTextBytes * 2) + json)), declared: 100);

        var facts = await Inspect.RunAsync(zip, BuiltInPackageFormat.Zip);

        facts.Values.ContainsKey(PackageFactKeys.NodeBuildScript).ShouldBeFalse();
        facts.Values.Keys.Where(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeEmpty();
    }

    // Rewrites the uncompressed size in the local and central headers of every entry, so the archive claims to hold less than it does.
    private static byte[] FalsifySizes(byte[] zip, int declared)
    {
        var copy = (byte[])zip.Clone();
        var size = BitConverter.GetBytes(declared);

        for (var i = 0; i < copy.Length - 30; i++)
        {
            var local = copy[i] == 0x50 && copy[i + 1] == 0x4B && copy[i + 2] == 0x03 && copy[i + 3] == 0x04;
            var central = copy[i] == 0x50 && copy[i + 1] == 0x4B && copy[i + 2] == 0x01 && copy[i + 3] == 0x02;

            if (local) Array.Copy(size, 0, copy, i + 22, 4);
            if (central) Array.Copy(size, 0, copy, i + 24, 4);
        }

        return copy;
    }

    // === inspectors ===

    [Fact]
    public async Task Node_InvalidSurrogateInAString_IsAnErrorFact_NotAFailure()
    {
        var facts = await Inspect.ZipAsync(("package.json", """{"engines":{"node":"\ud800"}}"""));

        facts.Get(PackageFactKeys.NodeError).ShouldNotBeNullOrEmpty();
        facts.Values.Keys.Where(k => k.StartsWith(PackageFactKeys.InspectorFailedPrefix)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Context_ReadsAFileNoInspectorDeclared_WithAPassOfItsOwn()
    {
        var facts = await Inspect.RunAsync(TestArchives.Zip(("notes/readme.txt", "hello"), ("other.txt", "x")), BuiltInPackageFormat.Zip, new UndeclaredReadInspector());

        facts.Get("test.read").ShouldBe("hello");
    }

    [Fact]
    public void PackageFacts_IsValuesOnlyData()
    {
        typeof(PackageFacts).GetProperties().Select(p => p.Name).ShouldBe(["Values"]);
        typeof(PackageFacts).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).ShouldBeEmpty("recipes receive data, with no helper that works only at push time");
    }

    private sealed class CountingStream(long length) : System.IO.Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - BytesRead);

            Array.Fill(buffer, (byte)'a', offset, n);
            BytesRead += n;

            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LongMessageInspector : IPackageInspector
    {
        public Task InspectAsync(PackageInspectionContext context, CancellationToken ct) => throw new InvalidOperationException(new string('m', 100_000));
    }

    private sealed class FloodInspector : IPackageInspector
    {
        public Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
        {
            for (var i = 0; i < 200; i++) context.Set($"flood.{i:D3}", new string('x', PackageInspectionContext.MaxValueLength));

            return Task.CompletedTask;
        }
    }

    private sealed class UndeclaredReadInspector : IPackageInspector
    {
        public async Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
        {
            context.Set("test.read", await context.TryReadTextAsync("notes/readme.txt", ct) ?? "missing");
        }
    }
}
