using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Squid.Mcp.Tools;

internal static class ReadToolSchemas
{
    internal static Tool Create(string name, string description, JsonElement inputSchema,
        JsonElement outputSchema, params string[] required)
    {
        if (required.Length > 0)
            inputSchema = WithRequired(inputSchema, required);

        return new Tool
        {
            Name = name,
            Title = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
        };
    }

    private static JsonElement WithRequired(JsonElement schema, string[] required)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node["required"] = new JsonArray(required.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray());
        return JsonSerializer.SerializeToElement(node);
    }

    internal static JsonElement Input(params (string Name, JsonObject Schema)[] properties)
    {
        var values = new JsonObject();
        foreach (var property in properties) values[property.Name] = property.Schema;
        return JsonSerializer.SerializeToElement(ObjectSchema(values, []));
    }

    internal static JsonElement ListOutput(JsonObject itemSchema)
    {
        return Output(ObjectSchema(new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = itemSchema
            },
            ["nextCursor"] = NullableString()
        }, ["items", "nextCursor"]));
    }

    internal static JsonElement Output(JsonObject successSchema)
    {
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["oneOf"] = new JsonArray(successSchema, ErrorSchema())
        });
    }

    internal static JsonObject ErrorSchema()
    {
        return ObjectSchema(new JsonObject
        {
            ["code"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    ReadToolErrors.InvalidArgument,
                    ReadToolErrors.NotFoundOrForbidden,
                    ReadToolErrors.TemporaryFailure)
            },
            ["message"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1
            }
        }, ["code", "message"]);
    }

    internal static JsonObject ObjectSchema(JsonObject properties, string[] required)
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
            ["additionalProperties"] = false
        };
    }

    internal static JsonObject IdSchema()
    {
        return new JsonObject { ["type"] = "integer", ["minimum"] = 1 };
    }

    internal static JsonObject PageSizeSchema()
    {
        return new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["maximum"] = 100,
            ["default"] = 20
        };
    }

    internal static JsonObject CursorSchema()
    {
        return new JsonObject { ["type"] = "string", ["minLength"] = 1 };
    }

    internal static JsonObject KeywordSchema()
    {
        return new JsonObject { ["type"] = "string" };
    }

    internal static JsonObject StringSchema()
    {
        return new JsonObject { ["type"] = "string" };
    }

    internal static JsonObject DateTimeSchema()
    {
        return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
    }

    internal static JsonObject StateSchema()
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(
                "Pending", "Executing", "Success", "Failed", "Cancelling",
                "Cancelled", "TimedOut", "Paused", "Unknown")
        };
    }

    internal static JsonObject NullableString()
    {
        return new JsonObject { ["type"] = new JsonArray("string", "null") };
    }

    internal static JsonObject NullableDateTime()
    {
        return new JsonObject { ["type"] = new JsonArray("string", "null"), ["format"] = "date-time" };
    }

    internal static JsonObject NullSchema() => new() { ["type"] = "null" };
}
