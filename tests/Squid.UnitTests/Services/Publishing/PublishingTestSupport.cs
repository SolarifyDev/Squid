using System.IO;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Inspectors;
using Squid.Message.Models.Publishing;
using Squid.UnitTests.Services.Packages.BuiltIn;

namespace Squid.UnitTests.Services.Publishing;

/// <summary>Runs the real inspection service, with the real inspectors, over a real zip or tar.gz written to disk.</summary>
internal static class Inspect
{
    public static IPackageInspector[] AllInspectors() => [new FileMarkerInspector(), new NodeInspector(), new SquidYamlInspector()];

    public static Task<PackageFacts> ZipAsync(params (string Name, string Content)[] entries) => RunAsync(TestArchives.Zip(entries), BuiltInPackageFormat.Zip);

    public static Task<PackageFacts> TarGzAsync(params (string Name, string Content)[] entries) => RunAsync(TestArchives.TarGz(entries), BuiltInPackageFormat.TarGz);

    public static async Task<PackageFacts> RunAsync(byte[] archive, BuiltInPackageFormat format, params IPackageInspector[] inspectors) => await RunAsync(archive, format, CancellationToken.None, inspectors);

    public static async Task<PackageFacts> RunAsync(byte[] archive, BuiltInPackageFormat format, CancellationToken ct, params IPackageInspector[] inspectors)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-inspect-{Guid.NewGuid():N}");

        await File.WriteAllBytesAsync(path, archive);

        try
        {
            var verifier = new BuiltInArchiveVerifier(new PackageInspectionService(inspectors.Length == 0 ? AllInspectors() : inspectors));

            return (await verifier.VerifyAsync(path, format, "pkg", ct)).Facts;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

internal sealed class StubRecipe(string kind, int score, params string[] reasons) : IDeployRecipe
{
    public string Kind { get; } = kind;

    public RecipeMatch Match(PackageFacts facts) => new() { Score = score, Reasons = reasons };
}

internal sealed class ThrowingInspector : IPackageInspector
{
    public Task InspectAsync(PackageInspectionContext context, CancellationToken ct) => throw new InvalidOperationException("boom");
}
