using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.Http;

public interface ISquidHttpClientFactory : IScopedDependency
{
    Task<T> GetAsync<T>(string requestUrl, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false);

    Task<T> PostAsync<T>(string requestUrl, HttpContent content, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false);
    
    Task<T> PutAsync<T>(string requestUrl, HttpContent content = null, TimeSpan? timeout = null, bool beginScope = false,
        Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false, CancellationToken cancellationToken = default);

    Task<T> DeleteAsync<T>(string requestUrl, HttpContent content = null, TimeSpan? timeout = null, bool beginScope = false,
        Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false, CancellationToken cancellationToken = default);
    
    Task<T> PostAsJsonAsync<T>(string requestUrl, object value, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false);
    
    Task<HttpResponseMessage> PostAsJsonAsync(string requestUrl, object value, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true);

    Task<T> PostAsStreamAsync<T>(string requestUrl, object value, CancellationToken cancellationToken,
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false);

    Task<T> PostAsMultipartAsync<T>(string requestUrl, Dictionary<string, string> formData, Dictionary<string, (byte[], string)> fileData,
        CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false);

    HttpClient CreateClient(TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null);
}

public class SquidHttpClientFactory : ISquidHttpClientFactory
{
    private readonly ILifetimeScope _scope;

    public SquidHttpClientFactory(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public HttpClient CreateClient(TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null)
    {
        var scope = beginScope ? _scope.BeginLifetimeScope() : _scope;
        
        var canResolve = scope.TryResolve(out IHttpClientFactory httpClientFactory);
        
        var client = canResolve ? httpClientFactory.CreateClient() : new HttpClient();
        
        if (timeout != null)
            client.Timeout = timeout.Value;

        if (headers == null) return client;
        
        foreach (var header in headers)
        {
            client.DefaultRequestHeaders.Add(header.Key, header.Value);
        }

        return client;
    }

    public async Task<T> GetAsync<T>(string requestUrl, CancellationToken cancellationToken,
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            
            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Get, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);
            
        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }

    public async Task<T> PostAsync<T>(string requestUrl, HttpContent content, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .PostAsync(requestUrl, content, cancellationToken).ConfigureAwait(false);

            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Post, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);
            
        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }

    public async Task<T> PutAsync<T>(string requestUrl, HttpContent content = null, TimeSpan? timeout = null, bool beginScope = false,
        Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false, CancellationToken cancellationToken = default)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .PutAsync(requestUrl, content, cancellationToken).ConfigureAwait(false);

            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Put, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);
        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }
    
    public async Task<T> DeleteAsync<T>(string requestUrl, HttpContent content = null, TimeSpan? timeout = null, bool beginScope = false,
        Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false, CancellationToken cancellationToken = default)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .DeleteAsync(requestUrl, cancellationToken).ConfigureAwait(false);

            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Delete, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);
        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }

    public async Task<T> PostAsJsonAsync<T>(string requestUrl, object value, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .PostAsJsonAsync(requestUrl, value, cancellationToken).ConfigureAwait(false);
            
            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Post, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);
            
        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }
    
    public async Task<HttpResponseMessage> PostAsJsonAsync(string requestUrl, object value, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
            await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .PostAsJsonAsync(requestUrl, value, cancellationToken).ConfigureAwait(false), cancellationToken, shouldLogError).ConfigureAwait(false);
    }
    
    public async Task<T> PostAsStreamAsync<T>(string requestUrl, object value, CancellationToken cancellationToken, 
        TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var jsonContent = JsonSerializer.Serialize(value, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
            
            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            
            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Post, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);
            
        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }
    
    public async Task<T> PostAsMultipartAsync<T>(string requestUrl, Dictionary<string, string> formData, Dictionary<string, (byte[], string)> fileData,
        CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false)
    {
        return await SafelyProcessRequestAsync(requestUrl, async () =>
        {
            var multipartContent = new MultipartFormDataContent();

            foreach (var data in formData)
            {
                multipartContent.Add(new StringContent(data.Value), data.Key);
            }

            foreach (var file in fileData)
            {
                multipartContent.Add(new ByteArrayContent(file.Value.Item1), file.Key, file.Value.Item2);
            }

            var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = multipartContent
            };

            var response = await CreateClient(timeout: timeout, beginScope: beginScope, headers: headers)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            return await ReadAndLogResponseAsync<T>(requestUrl, HttpMethod.Post, response, cancellationToken, isNeedToReadErrorContent).ConfigureAwait(false);

        }, cancellationToken, shouldLogError).ConfigureAwait(false);
    }
    
    private static async Task<T> ReadAndLogResponseAsync<T>(string requestUrl, HttpMethod httpMethod,
        HttpResponseMessage response, CancellationToken cancellationToken, bool isNeedToReadErrorContent = false)
    {
        // Read body as string once — stream can only be consumed once
        var bodyString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && !isNeedToReadErrorContent)
        {
            LogHttpError(requestUrl, httpMethod, response, bodyString);
            return default;
        }

        try
        {
            return DeserializeBody<T>(bodyString);
        }
        catch
        {
            LogHttpError(requestUrl, httpMethod, response, bodyString);
            return default;
        }
    }

    private static T DeserializeBody<T>(string bodyString)
    {
        if (typeof(T) == typeof(string))
            return (T)(object)bodyString;

        if (typeof(T) == typeof(byte[]))
            return (T)(object)Encoding.UTF8.GetBytes(bodyString);

        return JsonSerializer.Deserialize<T>(bodyString);
    }

    private static void LogHttpError(string requestUrl, HttpMethod httpMethod, HttpResponseMessage response, string bodyString)
    {
        Log.Error("Squid http {Method} {Url} error, Status: {StatusCode}, As string: {ResponseAsString}",
            httpMethod.ToString(), requestUrl, (int)response.StatusCode, bodyString);
    }
    
    private static async Task<T> SafelyProcessRequestAsync<T>(string requestUrl, Func<Task<T>> func, CancellationToken cancellationToken, bool shouldLogError = true)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await func().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Log.Warning(ex, "Request timed out for {RequestUrl}", requestUrl);
            return default;
        }
        catch (Exception ex)
        {
            if (shouldLogError)
                Log.Error(ex, "Error on requesting {RequestUrl}", requestUrl);
            else
                Log.Warning(ex, "Error on requesting {RequestUrl}", requestUrl);

            return default;
        }
    }
}