namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// Single source of truth for the <c>CapabilitiesResponse.SupportedServices</c> strings that
/// describe a ScriptService wire version. The agent advertises them; the server matches them.
/// Renaming a value breaks the capability handshake between mixed-version agents and servers.
/// </summary>
public static class ScriptServiceCapabilities
{
    public const string V2 = "IScriptService/v2";
}
