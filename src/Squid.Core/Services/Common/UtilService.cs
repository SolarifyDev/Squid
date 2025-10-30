using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Squid.Core.Services.Common;

public static class UtilService
{
    public static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();

        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] CompressToGzip<T>(T data)
    {
        var json = JsonConvert.SerializeObject(data);

        using var ms = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
        using (var sw = new StreamWriter(gzip))
        {
            sw.Write(json);
        }

        return ms.ToArray();
    }

    public static T DecompressFromGzip<T>(byte[] compressedData)
    {
        using var ms = new MemoryStream(compressedData);
        using var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var sr = new StreamReader(gzip);

        var json = sr.ReadToEnd();

        return JsonConvert.DeserializeObject<T>(json);
    }
}
