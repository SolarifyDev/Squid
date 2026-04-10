namespace Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

public sealed record ParsedServiceMessage(
    ServiceMessageKind Kind,
    string Verb,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string GetAttribute(string key)
        => Attributes.TryGetValue(key, out var value) ? value : null;

    public bool HasAttribute(string key)
        => Attributes.ContainsKey(key);
}
