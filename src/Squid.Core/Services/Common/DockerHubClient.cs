using System.Diagnostics;

namespace Squid.Core.Services.Common;

/// <summary>
/// Docker Hub 私人仓库客户端,用于下载私人镜像
/// </summary>
public class DockerHubClient
{
    private readonly string _username;
    private readonly string _password;
    private readonly string _dockerCommand;
    private readonly int _timeoutSeconds;
    private readonly int _maxRetries;

    public DockerHubClient(
        string username,
        string password,
        string dockerCommand = "docker",
        int timeoutSeconds = 300,  // 默认 5 分钟超时
        int maxRetries = 3)        // 默认重试 3 次
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("用户名不能为空", nameof(username));

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("密码不能为空", nameof(password));

        _username = username;
        _password = password;
        _dockerCommand = dockerCommand;
        _timeoutSeconds = timeoutSeconds;
        _maxRetries = maxRetries;
    }

    /// <summary>
    /// 登录到 Docker Hub (带重试机制)
    /// </summary>
    public async Task<bool> LoginAsync()
    {
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            Console.WriteLine($"正在登录 Docker Hub (用户: {_username}) - 尝试 {attempt}/{_maxRetries}...");

            var processInfo = new ProcessStartInfo
            {
                FileName = _dockerCommand,
                Arguments = "login -u " + _username + " --password-stdin",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                Console.WriteLine("❌ 无法启动 Docker 进程");
                return false;
            }

            await process.StandardInput.WriteLineAsync(_password);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            // 使用超时等待
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(cts.Token);

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode == 0)
                {
                    Console.WriteLine("✅ 登录成功");
                    return true;
                }
                else
                {
                    Console.WriteLine($"❌ 登录失败: {error}");

                    if (attempt < _maxRetries)
                    {
                        int waitSeconds = attempt * 2; // 递增等待时间
                        Console.WriteLine($"⏳ 等待 {waitSeconds} 秒后重试...");
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"⏱️  登录超时 ({_timeoutSeconds} 秒)");
                process.Kill(true);

                if (attempt < _maxRetries)
                {
                    int waitSeconds = attempt * 2;
                    Console.WriteLine($"⏳ 等待 {waitSeconds} 秒后重试...");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                }
            }
        }

        Console.WriteLine("❌ 登录失败: 已达到最大重试次数");
        return false;
    }

    /// <summary>
    /// 拉取 Docker 镜像
    /// </summary>
    /// <param name="imageName">镜像名称,格式: username/repository:tag</param>
    public async Task<bool> PullImageAsync(string imageName)
    {
        Console.WriteLine($"正在拉取镜像: {imageName}...");
        
        var processInfo = new ProcessStartInfo
        {
            FileName = _dockerCommand,
            Arguments = $"pull {imageName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            Console.WriteLine("❌ 无法启动 Docker 进程");
            return false;
        }

        // 实时输出拉取进度
        var outputTask = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                    Console.WriteLine($"  {line}");
            }
        });

        var errorTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                    Console.WriteLine($"  ⚠️  {line}");
            }
        });

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            Console.WriteLine($"✅ 镜像拉取成功: {imageName}");
            return true;
        }
        else
        {
            Console.WriteLine($"❌ 镜像拉取失败");
            return false;
        }
    }

    /// <summary>
    /// 保存 Docker 镜像为 tar 文件
    /// </summary>
    /// <param name="imageName">镜像名称</param>
    /// <param name="outputPath">输出文件路径</param>
    public async Task<bool> SaveImageAsync(string imageName, string outputPath)
    {
        Console.WriteLine($"正在保存镜像到: {outputPath}...");
        
        // 确保输出目录存在
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = _dockerCommand,
            Arguments = $"save -o \"{outputPath}\" {imageName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            Console.WriteLine("❌ 无法启动 Docker 进程");
            return false;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            Console.WriteLine($"✅ 镜像已保存到: {outputPath}");
            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine($"📦 文件大小: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
            return true;
        }
        else
        {
            Console.WriteLine($"❌ 保存镜像失败: {error}");
            return false;
        }
    }

    /// <summary>
    /// 删除本地 Docker 镜像
    /// </summary>
    /// <param name="imageName">镜像名称</param>
    public async Task<bool> RemoveImageAsync(string imageName)
    {
        Console.WriteLine($"正在删除镜像: {imageName}...");

        var processInfo = new ProcessStartInfo
        {
            FileName = _dockerCommand,
            Arguments = $"rmi {imageName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            Console.WriteLine("❌ 无法启动 Docker 进程");
            return false;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            Console.WriteLine($"✅ 镜像已删除: {imageName}");
            return true;
        }
        else
        {
            Console.WriteLine($"⚠️  删除镜像失败或镜像不存在: {error}");
            return false;
        }
    }

    /// <summary>
    /// 登出 Docker Hub
    /// </summary>
    public async Task<bool> LogoutAsync()
    {
        Console.WriteLine("正在登出 Docker Hub...");

        var processInfo = new ProcessStartInfo
        {
            FileName = _dockerCommand,
            Arguments = "logout",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            Console.WriteLine("❌ 无法启动 Docker 进程");
            return false;
        }

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            Console.WriteLine("✅ 已登出");
            return true;
        }
        else
        {
            Console.WriteLine("⚠️  登出失败");
            return false;
        }
    }

    /// <summary>
    /// 下载私人镜像的完整流程
    /// </summary>
    /// <param name="imageName">镜像名称</param>
    /// <param name="outputPath">输出文件路径</param>
    /// <param name="removeAfterSave">保存后是否删除本地镜像</param>
    public async Task<bool> DownloadPrivateImageAsync(string imageName, string outputPath, bool removeAfterSave = true)
    {
        try
        {
            // 1. 登录
            if (!await LoginAsync())
                return false;

            // 2. 拉取镜像
            if (!await PullImageAsync(imageName))
                return false;

            // 3. 保存镜像
            if (!await SaveImageAsync(imageName, outputPath))
                return false;

            // 4. 可选:删除本地镜像以节省空间
            if (removeAfterSave)
            {
                await RemoveImageAsync(imageName);
            }

            // 5. 登出
            await LogoutAsync();

            Console.WriteLine($"\n🎉 下载完成!");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 发生错误: {ex.Message}");
            return false;
        }
    }
}

