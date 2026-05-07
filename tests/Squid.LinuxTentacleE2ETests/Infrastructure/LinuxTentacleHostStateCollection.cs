namespace Squid.LinuxTentacleE2ETests.Infrastructure;

/// <summary>
/// xUnit collection definition that SERIALIZES test classes touching
/// shared Linux host state — sudo invocations, /etc/squid-tentacle,
/// /var/lib/squid-tentacle, /usr/local/bin/squid-tentacle, /etc/sudoers.d,
/// /etc/systemd/system, system users, and apt sources.
///
/// <para>By default xUnit runs different test classes IN PARALLEL on
/// separate threads. For tests that modify shared host state, parallel
/// execution races: two upgrade tests both writing
/// /usr/local/bin/squid-tentacle, two install tests competing on
/// /etc/sudoers.d/squid-tentacle-upgrade, etc. Even when each test's
/// fixture cleans up its own state, the assertion phase can observe
/// leftover state from a CONCURRENTLY-RUNNING test.</para>
///
/// <para>Caught by J.M.L.A.1 first runner: install-script test's
/// "no /usr/local/bin/squid-tentacle after failed install" reverse-
/// assert tripped on a leftover symlink from a parallel upgrade test
/// (whose Phase B creates the symlink and whose fixture's prior
/// Dispose didn't rm it). Two complementary fixes:
/// <list type="number">
///   <item>This collection — guarantees only one of these classes
///         runs at a time, so no concurrent host-state observation.</item>
///   <item>LinuxLifecycleContext.Dispose now also rm's the symlink
///         (cleanup completeness — even on assertion-failure paths).</item>
/// </list></para>
///
/// <para>Test classes that don't touch shared host state (e.g.
/// UpgradeLinuxScriptE2ETests which only does string parsing on the
/// rendered .sh) do NOT need this collection — they remain free to
/// parallelise.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LinuxTentacleHostStateCollection
{
    public const string Name = "LinuxTentacleHostState";
}
