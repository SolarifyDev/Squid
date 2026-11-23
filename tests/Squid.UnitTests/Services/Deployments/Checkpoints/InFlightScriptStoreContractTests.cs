using System.Linq;
using System.Reflection;
using Squid.Core.Services.Deployments.Checkpoints;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// The protocol-version members of <see cref="IInFlightScriptStore"/> had default bodies, so an implementer that did not override
/// them silently recorded every V2 dispatch as V1 (and answered "unknown" for every protocol read). They are abstract now: an
/// implementer that forgets them does not compile. A default body here would compile for such an implementer and bring the bug back.
/// </summary>
public class InFlightScriptStoreContractTests
{
    [Theory]
    [InlineData("RecordDispatchedAsync", 5)]
    [InlineData("TryGetProtocolAsync", 3)]
    public void VersionMembers_HaveNoDefaultImplementation(string name, int parameterCount)
    {
        var method = typeof(IInFlightScriptStore).GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, ParametersFor(name, parameterCount), null);

        method.ShouldNotBeNull();
        method.IsAbstract.ShouldBeTrue($"{name} must be implemented, never defaulted to a V1 answer");
    }

    [Fact]
    public void TheRealStore_PersistsTheVersion_ItOverridesBothMembers()
    {
        var map = typeof(InFlightScriptStore).GetInterfaceMap(typeof(IInFlightScriptStore));

        foreach (var interfaceMethod in map.InterfaceMethods.Where(m => m.Name is "TryGetProtocolAsync" || (m.Name == "RecordDispatchedAsync" && m.GetParameters().Length == 5)))
        {
            var index = Array.IndexOf(map.InterfaceMethods, interfaceMethod);

            map.TargetMethods[index].DeclaringType.ShouldBe(typeof(InFlightScriptStore));
        }
    }

    private static Type[] ParametersFor(string name, int count) => name == "TryGetProtocolAsync"
        ? new[] { typeof(int), typeof(DispatchSlot), typeof(CancellationToken) }
        : new[] { typeof(int), typeof(DispatchSlot), typeof(string), typeof(Squid.Message.Models.Deployments.Execution.ScriptProtocolVersion), typeof(CancellationToken) };
}
