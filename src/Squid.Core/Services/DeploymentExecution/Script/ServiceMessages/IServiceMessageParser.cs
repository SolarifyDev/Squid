using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

public interface IServiceMessageParser : ISingletonDependency
{
    Dictionary<string, OutputVariable> ParseOutputVariables(IEnumerable<string> logLines);

    IReadOnlyList<ParsedServiceMessage> ParseMessages(IEnumerable<string> logLines);

    ParsedServiceMessage TryParseMessage(string line);

    OutputVariable TryParseOutputVariable(string line);
}
