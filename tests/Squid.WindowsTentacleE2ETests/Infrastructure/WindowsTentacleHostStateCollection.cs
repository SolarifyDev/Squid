namespace Squid.WindowsTentacleE2ETests.Infrastructure;

/// <summary>
/// xUnit collection definition that SERIALIZES test classes touching
/// shared Windows host state — the per-process <c>InstanceRegistry</c>
/// (writes <c>instances.json</c> at <c>%ProgramData%\Squid Tentacle\</c>),
/// per-instance config files, and per-instance certs directories.
///
/// <para>By default xUnit runs different test classes IN PARALLEL on
/// separate threads. <c>InstanceRegistry.CreateForCurrentProcess()</c>
/// is process-wide, not test-isolated — concurrent
/// <c>registry.Add()</c> / <c>registry.Remove()</c> calls from
/// parallel tests race on the underlying <c>instances.json</c> file.</para>
///
/// <para>Caught by Phase 12.M.W.D first runner: when
/// <c>TentacleDiagnosticCommandE2ETests</c> ran in parallel with
/// <c>TentacleRegisterE2ETests</c>, register's pre-created instance
/// entry was getting stomped by diagnostic tests' concurrent
/// instances.json rewrites — register would then throw
/// <c>InvalidOperationException</c>: "Instance does not exist. Run
/// 'squid-tentacle create-instance --instance {name}' first."</para>
///
/// <para>Apply this collection to every Windows test class that adds /
/// removes / queries entries in <c>instances.json</c> via
/// <c>InstanceRegistry</c> OR drives a command that does so internally
/// (register, create-instance, delete-instance, service install/uninstall
/// --purge).</para>
///
/// <para>Mirrors <c>LinuxTentacleHostStateCollection</c> from the Linux
/// project (introduced for the same race-protection reason).</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class WindowsTentacleHostStateCollection
{
    public const string Name = "WindowsTentacleHostState";
}
