using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Common;

/// <summary>
/// xUnit collection marker — every test class that mutates process-wide
/// env vars (specifically <c>SQUID_CALAMARI_REWRITER_MAX_FILE_SIZE_MB</c>)
/// MUST opt into this collection so they run serially. Without it, xUnit
/// parallelizes across classes by default and tests setting / clearing
/// the env var on different threads produce flaky cross-test interference.
///
/// <para>Usage: <c>[Collection(RewriterEnvVarSerialCollection.Name)]</c>
/// on the test class.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class RewriterEnvVarSerialCollection
{
    public const string Name = "RewriterEnvVar serial";
}
