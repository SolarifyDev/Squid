using System.Net.Http.Headers;
using System.Text;

namespace Squid.Core.Services.Common;

public class GithubPackageDownloader
{
    private readonly string _username;
    private readonly HttpClient _httpClient;
    private readonly string _mirrorUrlTemplate;

    public GithubPackageDownloader(string username, string token)
        : this(username, token, null)
    {
    }

    public GithubPackageDownloader(string username, string token, string mirrorUrlTemplate)
    {
        _username = username;
        _httpClient = new HttpClient();
        _mirrorUrlTemplate = mirrorUrlTemplate;

        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", authValue);
    }

    public async Task<MemoryStream> DownloadPackageAsync(string packageId, string version)
    {
        var urls = new List<string>();

        if (!string.IsNullOrWhiteSpace(_mirrorUrlTemplate))
        {
            var mirrorUrl = _mirrorUrlTemplate
                .Replace("{username}", _username, StringComparison.Ordinal)
                .Replace("{packageId}", packageId, StringComparison.Ordinal)
                .Replace("{version}", version, StringComparison.Ordinal);

            urls.Add(mirrorUrl);
        }

        urls.Add($"https://nuget.pkg.github.com/{_username}/download/{packageId}/{version}/{packageId}.{version}.nupkg");
        urls.Add($"https://nuget.pkg.github.com/{_username.ToLower()}/download/{packageId.ToLower()}/{version}/{packageId.ToLower()}.{version}.nupkg");
        urls.Add($"https://nuget.pkg.github.com/download/{packageId}/{version}/{packageId}.{version}.nupkg");

        foreach (var url in urls)
        {
            try
            {
                Log.Information("尝试直接下载: {Url}", url);

                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var memoryStream = new MemoryStream();

                    await response.Content.CopyToAsync(memoryStream).ConfigureAwait(false);

                    memoryStream.Position = 0;

                    Log.Information("成功下载包，大小: {PackageSize} 字节", memoryStream.Length);

                    return memoryStream;
                }

                Log.Information("下载失败，状态码: {StatusCode}", response.StatusCode);
            }
            catch (Exception ex)
            {
                Log.Information(ex, "下载异常");
            }
        }

        throw new Exception($"无法通过直接下载方式获取包 {packageId} {version}");
    }
}