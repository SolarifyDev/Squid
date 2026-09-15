using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mediator.Net.Pipeline;
using Squid.Core.Extensions;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Middlewares.Logging;

public class LoggerSpecification<TContext> : IPipeSpecification<TContext>
    where TContext : IContext<IMessage>
{
    private static readonly JsonSerializerOptions SafeJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 16
    };

    private readonly ILogger _logger;

    public LoggerSpecification(ILogger logger)
    {
        _logger = logger;
    }

    public bool ShouldExecute(TContext context, CancellationToken cancellationToken)
    {
        return true;
    }

    public Task BeforeExecute(TContext context, CancellationToken cancellationToken)
    {
        _logger.Information("----- Handling message {MessageName} ({@Message})", context.Message.GetGenericTypeName(),
            GetSafeMessage(context.Message));
        return Task.CompletedTask;
    }

    private static object GetSafeMessage(IMessage message)
    {
        if (message is UploadOctopusImportCommand upload)
        {
            return new
            {
                upload.SpaceId,
                upload.FileName,
                upload.ContentType,
                upload.SizeBytes
            };
        }

        return GetSafeValue(message);
    }

    private static object GetSafeValue(object value)
    {
        if (value == null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(value, value.GetType(), SafeJsonOptions);
            var redactedJson = OctopusImportRedaction.RedactJson(json);
            using var document = JsonDocument.Parse(redactedJson);
            return ToLogValue(document.RootElement);
        }
        catch (Exception) when (value is not string)
        {
            return new { Type = value.GetType().FullName };
        }
    }

    private static object ToLogValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => ToLogValue(property.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ToLogValue)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public Task Execute(TContext context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task AfterExecute(TContext context, CancellationToken cancellationToken)
    {
        _logger.Information("----- Message {MessageName} handled - response: {@Response}",
            context.Message.GetGenericTypeName(), GetSafeValue(context.Result));
        
        return Task.CompletedTask;
    }

    public Task OnException(Exception ex, TContext context)
    {
        ExceptionDispatchInfo.Capture(ex).Throw();
        return Task.CompletedTask;
    }
}
