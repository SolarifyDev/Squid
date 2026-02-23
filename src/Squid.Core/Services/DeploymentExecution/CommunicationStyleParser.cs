using System.Text.Json;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution;

public static class CommunicationStyleParser
{
    public static CommunicationStyle Parse(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return CommunicationStyle.Unknown;

        try
        {
            using var doc = JsonDocument.Parse(endpointJson);

            if (!doc.RootElement.TryGetProperty("CommunicationStyle", out var prop) &&
                !doc.RootElement.TryGetProperty("communicationStyle", out prop))
                return CommunicationStyle.Unknown;

            var value = prop.GetString();

            return Enum.TryParse<CommunicationStyle>(value, ignoreCase: true, out var style)
                ? style
                : CommunicationStyle.Unknown;
        }
        catch
        {
            return CommunicationStyle.Unknown;
        }
    }
}
