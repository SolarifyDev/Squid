using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Squid.Core.Services.Common;

public interface IYamlNuGetPacker : IScopedDependency
{
    byte[] CreateNuGetPackageFromYamlBytes(Dictionary<string, byte[]> yamlFiles, string version = null, string packageId = null);

    byte[] CreateNuGetPackageFromYamlStreams(Dictionary<string, Stream> yamlFiles, string version = null, string packageId = null);
}

public class YamlNuGetPacker : IYamlNuGetPacker
{
    private readonly Assembly _assembly;
    private readonly string _resourceName;

    public YamlNuGetPacker(Assembly assembly = null, string resourceName = "Squid.nuspec")
    {
        _assembly = assembly ?? Assembly.GetExecutingAssembly();
        _resourceName = resourceName;
    }

    /// <summary>
    /// 从YAML文件字节数组字典创建NuGet包
    /// </summary>
    /// <param name="yamlFiles">键值对：文件名 -> YAML内容字节数组</param>
    /// <param name="version">包版本</param>
    /// <param name="packageId">包ID</param>
    public byte[] CreateNuGetPackageFromYamlBytes(Dictionary<string, byte[]> yamlFiles, string version = null, string packageId = null)
    {
        using var memoryStream = new MemoryStream();
        CreateNuGetPackageFromYamlBytes(yamlFiles, memoryStream, version, packageId);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// 从YAML文件流字典创建NuGet包
    /// </summary>
    /// <param name="yamlFiles">键值对：文件名 -> YAML内容流</param>
    /// <param name="version">包版本</param>
    /// <param name="packageId">包ID</param>
    public byte[] CreateNuGetPackageFromYamlStreams(Dictionary<string, Stream> yamlFiles, string version = null, string packageId = null)
    {
        using var memoryStream = new MemoryStream();
        CreateNuGetPackageFromYamlStreams(yamlFiles, memoryStream, version, packageId);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// 从YAML文件字节数组创建NuGet包到指定流
    /// </summary>
    public void CreateNuGetPackageFromYamlBytes(Dictionary<string, byte[]> yamlFiles, Stream outputStream, string version = null, string packageId = null)
    {
        // 从嵌入资源读取.nuspec模板
        string nuspecContent = GetEmbeddedNuspecTemplate();
        
        // 更新版本和包ID
        nuspecContent = UpdateNuspecContent(nuspecContent, version, packageId);

        // 创建NuGet包
        CreateNuGetPackageWithYamlBytes(outputStream, nuspecContent, yamlFiles);
    }

    /// <summary>
    /// 从YAML文件流创建NuGet包到指定流
    /// </summary>
    public void CreateNuGetPackageFromYamlStreams(Dictionary<string, Stream> yamlFiles, Stream outputStream, string version = null, string packageId = null)
    {
        // 从嵌入资源读取.nuspec模板
        string nuspecContent = GetEmbeddedNuspecTemplate();
        
        // 更新版本和包ID
        nuspecContent = UpdateNuspecContent(nuspecContent, version, packageId);

        // 创建NuGet包
        CreateNuGetPackageWithYamlStreams(outputStream, nuspecContent, yamlFiles);
    }

    private string GetEmbeddedNuspecTemplate()
    {
        var resourceNames = _assembly.GetManifestResourceNames();
        string actualResourceName = FindResourceName(resourceNames);
        
        if (string.IsNullOrEmpty(actualResourceName))
            throw new FileNotFoundException($"找不到嵌入资源: {_resourceName}");

        using var stream = _assembly.GetManifestResourceStream(actualResourceName);
        if (stream == null)
            throw new FileNotFoundException($"无法读取嵌入资源: {actualResourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string FindResourceName(string[] resourceNames)
    {
        // 精确匹配
        foreach (var name in resourceNames)
        {
            if (name.EndsWith(_resourceName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(_resourceName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        // 模糊匹配
        foreach (var name in resourceNames)
        {
            if (name.Contains(_resourceName) || _resourceName.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    private string UpdateNuspecContent(string nuspecContent, string version, string packageId)
    {
        var doc = XDocument.Parse(nuspecContent);
        var metadata = doc.Root?.Element("metadata");

        if (metadata != null)
        {
            if (!string.IsNullOrEmpty(packageId))
            {
                var idElement = metadata.Element("id");
                if (idElement != null) 
                    idElement.Value = packageId;
            }

            if (!string.IsNullOrEmpty(version))
            {
                var versionElement = metadata.Element("version");
                if (versionElement != null) 
                    versionElement.Value = version;
            }
        }

        return doc.ToString();
    }

    private void CreateNuGetPackageWithYamlBytes(Stream outputStream, string nuspecContent, Dictionary<string, byte[]> yamlFiles)
    {
        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, true);
        
        // 添加.nuspec文件到包根目录
        var nuspecEntry = archive.CreateEntry("Squid.nuspec", CompressionLevel.Optimal);
        using (var nuspecStream = new StreamWriter(nuspecEntry.Open()))
        {
            nuspecStream.Write(nuspecContent);
        }

        // 添加所有YAML文件到content目录
        foreach (var yamlFile in yamlFiles)
        {
            string entryName = $"content/{yamlFile.Key}";
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            
            using var entryStream = entry.Open();
            entryStream.Write(yamlFile.Value, 0, yamlFile.Value.Length);
        }
    }

    private void CreateNuGetPackageWithYamlStreams(Stream outputStream, string nuspecContent, Dictionary<string, Stream> yamlFiles)
    {
        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, true);
        
        // 添加.nuspec文件到包根目录
        var nuspecEntry = archive.CreateEntry("Squid.nuspec", CompressionLevel.Optimal);
        using (var nuspecStream = new StreamWriter(nuspecEntry.Open()))
        {
            nuspecStream.Write(nuspecContent);
        }

        // 添加所有YAML文件到content目录
        foreach (var yamlFile in yamlFiles)
        {
            string entryName = $"content/{yamlFile.Key}";
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            
            using var entryStream = entry.Open();
            yamlFile.Value.Position = 0; // 确保流位置在开始
            yamlFile.Value.CopyTo(entryStream);
        }
    }
}