using System.Text.Json.Nodes;
using Halibut;
using Halibut.Diagnostics;

namespace Squid.Core.Services.Machines;

public static class EndpointJsonHelper
{
    public static string GetField(string endpointJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(endpointJson)) return null;

        try
        {
            var node = JsonNode.Parse(endpointJson);
            return node?[fieldName]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static string UpdateField(string endpointJson, string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(endpointJson)) return null;

        try
        {
            var node = JsonNode.Parse(endpointJson);
            if (node is not JsonObject obj) return endpointJson;

            obj[fieldName] = value;
            return obj.ToJsonString();
        }
        catch
        {
            return endpointJson;
        }
    }

    public static ServiceEndPoint ParseHalibutEndpoint(string endpointJson)
    {
        if (string.IsNullOrWhiteSpace(endpointJson)) return null;

        try
        {
            var subscriptionId = GetField(endpointJson, "SubscriptionId");
            var thumbprint = GetField(endpointJson, "Thumbprint");

            if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(thumbprint))
                return null;

            var uri = $"poll://{subscriptionId}/";

            return new ServiceEndPoint(uri, thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        }
        catch
        {
            return null;
        }
    }

    public static ServiceEndPoint ParseTentacleListeningEndpoint(string endpointJson)
    {
        if (string.IsNullOrWhiteSpace(endpointJson)) return null;

        try
        {
            var uri = GetField(endpointJson, "Uri");
            var thumbprint = GetField(endpointJson, "Thumbprint");

            if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(thumbprint))
                return null;

            return new ServiceEndPoint(new Uri(uri), thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        }
        catch
        {
            return null;
        }
    }
}
