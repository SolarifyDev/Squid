using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variable;

public interface ISnapshotCompressionService : IScopedDependency
{
    byte[] CompressSnapshot(VariableSetSnapshotData data);
    VariableSetSnapshotData DecompressSnapshot(byte[] compressedData);
    int EstimateUncompressedSize(VariableSetSnapshotData data);
}

public class SnapshotCompressionService : ISnapshotCompressionService
{
    private readonly JsonSerializerOptions _jsonOptions;

    public SnapshotCompressionService()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public byte[] CompressSnapshot(VariableSetSnapshotData data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);

        var jsonBytes = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }
        
        return output.ToArray();
    }
    
    public VariableSetSnapshotData DecompressSnapshot(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        
        gzip.CopyTo(output);
        var jsonBytes = output.ToArray();

        var json = Encoding.UTF8.GetString(jsonBytes);

        return JsonSerializer.Deserialize<VariableSetSnapshotData>(json, _jsonOptions);
    }

    public int EstimateUncompressedSize(VariableSetSnapshotData data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        return Encoding.UTF8.GetByteCount(json);
    }
}
