using Squid.Message.Models.Setup;
using StackExchange.Redis;

namespace Squid.Core.Setup.Install;

public static class RedisConnectionTester
{
    public static async Task<RedisConnectionTestResult> TestAsync(string connectionString)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = true;
            options.ConnectTimeout = 5000;
            options.ConnectRetry = 0;

            await using var connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
            var latency = await connection.GetDatabase().PingAsync().ConfigureAwait(false);

            return new RedisConnectionTestResult { Success = true, Message = $"Connected (ping {latency.TotalMilliseconds:0} ms)" };
        }
        catch (Exception ex)
        {
            return new RedisConnectionTestResult { Success = false, Message = ex.Message };
        }
    }
}
