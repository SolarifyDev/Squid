using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for the parsed-source cache in the real container: each request of an import flow runs in its own
/// lifetime scope, and the source one scope parsed (a REST history bundle can hold gigabytes) is what the next scope plans from.
/// </summary>
[Trait("Category", "OctopusImport")]
public sealed class OctopusImportExtractionCacheIntegrationTests : TestBase
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"squid-it-extraction-cache-{Guid.NewGuid():N}.json");

    public OctopusImportExtractionCacheIntegrationTests()
        : base("OctopusImportExtractionCache", "squid_it_octopus_import_extraction_cache")
    {
        File.WriteAllText(_path, "{}");
    }

    [Fact]
    public async Task SourceParsedInOneRequestScope_IsWhatTheNextRequestScopePlansFrom()
    {
        IOctopusImportExtractionCache firstCache = null;
        OctopusInputExtractionResult parsed = null;

        try
        {
            await Run<IOctopusImportExtractionCache, IOctopusImportSourceReader>(async (cache, reader) =>
            {
                firstCache = cache;
                parsed = await cache.GetOrReadAsync(_path, () => reader.ReadAsync(_path));
            });

            await Run<IOctopusImportExtractionCache, IOctopusImportPlanningPipeline>(async (cache, pipeline) =>
            {
                cache.ShouldBeSameAs(firstCache, "the cache must be one per process, not one per request scope");

                var again = await cache.GetOrReadAsync(_path, () => throw new InvalidOperationException($"{_path} was parsed again in a second request scope"));
                again.ShouldBeSameAs(parsed);

                await pipeline.BuildGraphAsync(_path);
            });
        }
        finally
        {
            File.Delete(_path);
        }
    }
}
