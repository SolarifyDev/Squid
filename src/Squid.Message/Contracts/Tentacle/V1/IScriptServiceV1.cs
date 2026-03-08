namespace Squid.Message.Contracts.Tentacle.V1;

/// <summary>
/// V1 versioned alias for IScriptService.
/// When a breaking change is needed, create IScriptServiceV2 in V2/ namespace
/// and register both in DelegateServiceFactory. Use ICapabilitiesService to detect
/// which version the Tentacle supports.
///
/// Versioning convention:
/// - New versions add a new interface (IScriptServiceV2, etc.)
/// - Old versions remain registered for backward compatibility
/// - Server queries ICapabilitiesService.GetCapabilities() to detect supported versions
/// - SupportedServices list uses format: "IScriptService/v1", "IScriptService/v2"
/// </summary>
public interface IScriptServiceV1 : IScriptService;
