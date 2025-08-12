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
            WriteIndented = false, // 紧凑格式
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public byte[] CompressSnapshot(VariableSetSnapshotData data)
    {
        // 1. 序列化为JSON
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        
        // 2. 转换为UTF-8字节
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        
        // 3. GZIP压缩
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }
        
        return output.ToArray();
    }
    
    public VariableSetSnapshotData DecompressSnapshot(byte[] compressedData)
    {
        // 1. GZIP解压
        using var input = new MemoryStream(compressedData);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        
        gzip.CopyTo(output);
        var jsonBytes = output.ToArray();
        
        // 2. 转换为字符串
        var json = Encoding.UTF8.GetString(jsonBytes);
        
        // 3. 反序列化
        return JsonSerializer.Deserialize<VariableSetSnapshotData>(json, _jsonOptions);
    }

    public int EstimateUncompressedSize(VariableSetSnapshotData data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        return Encoding.UTF8.GetByteCount(json);
    }
}
