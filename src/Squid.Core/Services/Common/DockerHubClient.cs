using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Squid.Core.Services.Common;

public class DockerHubClient
{
    private readonly string _username;
    private readonly string _password;
    private readonly int _timeoutSeconds;
    private readonly int _maxRetries;
    private readonly DockerClient _dockerClient;

    public DockerHubClient(
        string username,
        string password,
        int timeoutSeconds = 300,
        int maxRetries = 3)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("用户名不能为空", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("密码不能为空", nameof(password));
        }

        _username = username;
        _password = password;
        _timeoutSeconds = timeoutSeconds;
        _maxRetries = maxRetries;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    public async Task<bool> LoginAsync()
    {
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            Log.Information("正在登录 Docker Hub (用户: {Username}) - 尝试 {Attempt}/{MaxRetries}...", _username, attempt, _maxRetries);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

                var imagesCreateParameters = new ImagesCreateParameters
                {
                    FromImage = "registry.hub.docker.com/library/alpine",
                    Tag = "latest"
                };

                var authConfig = new AuthConfig
                {
                    Username = _username,
                    Password = _password,
                    ServerAddress = "https://index.docker.io/v1/"
                };

                await _dockerClient.Images.CreateImageAsync(imagesCreateParameters, authConfig, new Progress<JSONMessage>(), cts.Token).ConfigureAwait(false);

                Log.Information("登录成功");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "登录 Docker Hub 失败 (用户: {Username})", _username);

                if (attempt < _maxRetries)
                {
                    var waitSeconds = attempt * 2;

                    Log.Information("等待 {WaitSeconds} 秒后重试", waitSeconds);

                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds)).ConfigureAwait(false);
                }
            }
        }

        Log.Information("登录失败: 已达到最大重试次数");

        return false;
    }

    public async Task<bool> PullImageAsync(string imageName)
    {
        Log.Information("正在拉取镜像: {ImageName}...", imageName);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

            var imagesCreateParameters = new ImagesCreateParameters
            {
                FromImage = imageName
            };

            var authConfig = new AuthConfig
            {
                Username = _username,
                Password = _password,
                ServerAddress = "https://index.docker.io/v1/"
            };

            await _dockerClient.Images.CreateImageAsync(imagesCreateParameters, authConfig, new Progress<JSONMessage>(), cts.Token).ConfigureAwait(false);

            Log.Information("镜像拉取成功: {ImageName}", imageName);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "镜像拉取失败: {ImageName}", imageName);

            return false;
        }
    }

    public async Task<bool> SaveImageAsync(string imageName, string outputPath)
    {
        Log.Information("正在保存镜像到: {OutputPath}...", outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

            using var imageStream = await _dockerClient.Images.SaveImageAsync(imageName, cts.Token).ConfigureAwait(false);

            await imageStream.CopyToAsync(fileStream, cts.Token).ConfigureAwait(false);

            var fileInfo = new FileInfo(outputPath);

            Log.Information("镜像已保存到: {OutputPath}, 文件大小: {FileSize} MB", outputPath, fileInfo.Length / 1024.0 / 1024.0);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存镜像失败: {ImageName}", imageName);

            return false;
        }
    }

    public async Task<bool> RemoveImageAsync(string imageName)
    {
        Log.Information("正在删除镜像: {ImageName}...", imageName);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

            await _dockerClient.Images.DeleteImageAsync(imageName, new ImageDeleteParameters(), cts.Token).ConfigureAwait(false);

            Log.Information("镜像已删除: {ImageName}", imageName);

            return true;
        }
        catch (DockerImageNotFoundException)
        {
            Log.Information("镜像不存在: {ImageName}", imageName);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除镜像失败: {ImageName}", imageName);

            return false;
        }
    }

    public Task<bool> LogoutAsync()
    {
        Log.Information("Docker Hub 使用的是基于请求的认证,无需显式登出");

        return Task.FromResult(true);
    }

    public async Task<bool> DownloadPrivateImageAsync(string imageName, string outputPath, bool removeAfterSave = true)
    {
        try
        {
            var loginResult = await LoginAsync().ConfigureAwait(false);

            if (!loginResult)
            {
                return false;
            }

            var pullResult = await PullImageAsync(imageName).ConfigureAwait(false);

            if (!pullResult)
            {
                return false;
            }

            var saveResult = await SaveImageAsync(imageName, outputPath).ConfigureAwait(false);

            if (!saveResult)
            {
                return false;
            }

            if (removeAfterSave)
            {
                await RemoveImageAsync(imageName).ConfigureAwait(false);
            }

            await LogoutAsync().ConfigureAwait(false);

            Log.Information("下载完成: {ImageName}", imageName);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载私人镜像时发生错误: {ImageName}", imageName);

            return false;
        }
    }
}

