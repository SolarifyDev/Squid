using Xunit;

namespace Squid.UnitTests.Halibut;

/// <summary>
/// Serialises every test class that mutates the process-wide SQUID_HALIBUT_* env vars so
/// parallel xunit classes cannot observe each other's overrides.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HalibutEnvVarCollection
{
    public const string Name = "HalibutEnvVars";
}
