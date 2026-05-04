namespace Squid.UnitTests.Support;

/// <summary>
/// P1-Phase12.E.6 — xUnit collection that disables parallelization across
/// every test class that mutates a process-global static handle. Without
/// serialisation, parallel cross-class execution races on shared globals
/// (Serilog <c>Log.Logger</c> setter, <c>ActivitySource.AddActivityListener</c>
/// registry) and pollutes captures across tests.
///
/// <para><b>Three documented failure modes this collection prevents:</b></para>
/// <list type="number">
///   <item><c>InvalidOperationException: Collection was modified; enumeration
///         operation may not execute</c> — Test A's <c>foreach sink.Events</c>
///         races with Test B's <c>Log.X</c> call (which Serilog routes
///         through whichever logger is currently <c>Log.Logger</c>, possibly
///         A's). The shared <c>List&lt;LogEvent&gt;</c> sink isn't
///         thread-safe; Add during foreach throws.</item>
///   <item>Cross-test event count drift — Test A expects N captured events
///         (its own writes); Test B's parallel <c>Log.X</c> writes add M
///         more to A's sink → A's assertion sees N+M.</item>
///   <item>Cross-test ActivityListener leakage — Test A registers a listener
///         filtering <c>source.Name == X</c>; Test B (different class) creates
///         activities on source X; A's <c>_captured</c> list collects B's
///         activities too → A's <c>ShouldHaveSingleItem</c> fails with
///         "had 8 items".</item>
/// </list>
///
/// <para><b>How to opt in:</b> tag the test class with
/// <c>[Collection(GlobalStateSerialisedCollection.Name)]</c>. xUnit then runs
/// all classes in this collection sequentially, eliminating the cross-class
/// race window.</para>
///
/// <para><b>Cost:</b> 8 test classes (~50 tests total) execute serially
/// instead of in parallel with each other. Per-class internal parallelism is
/// already off by xUnit default (sequential within one class). Other test
/// classes (~4900 tests) still parallelize freely. Net suite-time impact:
/// negligible.</para>
///
/// <para><b>Future maintenance rule:</b> any new test class that calls
/// <c>Log.Logger = </c> OR <c>ActivitySource.AddActivityListener</c> MUST
/// join this collection. Forgetting → flaky CI. The
/// <c>RunTimeProcessGlobalsAreOptedIntoSerialisation</c> contract test in
/// this support folder enumerates every UnitTests assembly type for those
/// two patterns and fails if any aren't tagged.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateSerialisedCollection
{
    /// <summary>
    /// Pinned per Rule 8: rename here without updating every <c>[Collection(...)]</c>
    /// site silently re-enables parallel races.
    /// </summary>
    public const string Name = "GlobalStateSerialised";
}
