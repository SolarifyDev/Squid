using System.Text;

namespace Squid.Core.Services.Common;

public class GithubPackageDownloader
{
    private readonly string _username;
    private readonly HttpClient _httpClient;

    public GithubPackageDownloader(string username, string token)
    {
        _username = username;
        _httpClient = new HttpClient();
        
        // 设置Basic Authentication
        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
    }

    public async Task<MemoryStream> DownloadPackageAsync(string packageId, string version)
    {
        // GitHub Packages 的直接下载URL格式
        var possibleUrls = new[]
        {
            $"https://nuget.pkg.github.com/{_username}/download/{packageId}/{version}/{packageId}.{version}.nupkg",
            $"https://nuget.pkg.github.com/{_username.ToLower()}/download/{packageId.ToLower()}/{version}/{packageId.ToLower()}.{version}.nupkg",
            $"https://nuget.pkg.github.com/download/{packageId}/{version}/{packageId}.{version}.nupkg"
        };

        foreach (var url in possibleUrls)
        {
            try
            {
                Console.WriteLine($"尝试直接下载: {url}");
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                
                if (response.IsSuccessStatusCode)
                {
                    var memoryStream = new MemoryStream();
                    await response.Content.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;
                    
                    Console.WriteLine($"成功下载包，大小: {memoryStream.Length} 字节");
                    return memoryStream;
                }
                else
                {
                    Console.WriteLine($"下载失败，状态码: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"下载异常: {ex.Message}");
            }
        }

        throw new Exception($"无法通过直接下载方式获取包 {packageId} {version}");
    }
}