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

    public static string GetEmbeddedScriptContent(string resourceFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resourceNames = assembly.GetManifestResourceNames();

        var resourceName = FindEmbeddedResourceName(resourceNames, resourceFileName);

        if (string.IsNullOrWhiteSpace(resourceName))
            throw new FileNotFoundException($"找不到嵌入资源脚本: {resourceFileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
            throw new FileNotFoundException($"无法读取嵌入资源脚本: {resourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static string FindEmbeddedResourceName(string[] resourceNames, string resourceFileName)
    {
        foreach (var name in resourceNames)
        {
            if (name.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(resourceFileName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        foreach (var name in resourceNames)
        {
            if (name.Contains(resourceFileName, StringComparison.OrdinalIgnoreCase) ||
                resourceFileName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return string.Empty;
    }
}
