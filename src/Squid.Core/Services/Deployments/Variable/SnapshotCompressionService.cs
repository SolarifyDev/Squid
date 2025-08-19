using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog.Formatting.Json;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variable;

public static class SnapshotCompressionService
{
    private static readonly JsonSerializerSettings _jsonOptions = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static byte[] CompressSnapshot(VariableSetSnapshotData data)
    {
        var json = JsonConvert.SerializeObject(data, _jsonOptions);

        var jsonBytes = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }
        
        return output.ToArray();
    }
    
    public static VariableSetSnapshotData DecompressSnapshot(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        
        gzip.CopyTo(output);
        var jsonBytes = output.ToArray();

        var json = Encoding.UTF8.GetString(jsonBytes);

        return JsonConvert.DeserializeObject<VariableSetSnapshotData>(json, _jsonOptions);
    }

    public static int EstimateUncompressedSize(VariableSetSnapshotData data)
    {
        var json = JsonConvert.SerializeObject(data, _jsonOptions);
        return Encoding.UTF8.GetByteCount(json);
    }
}
