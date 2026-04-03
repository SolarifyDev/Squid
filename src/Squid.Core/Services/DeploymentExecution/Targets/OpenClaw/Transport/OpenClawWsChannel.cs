using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

internal enum WsChannelState { Disconnected, Connecting, Connected, Disposed }

internal record WsResponse(bool Ok, JsonElement? Payload, string ErrorCode, string ErrorMessage);

internal record WsEvent(string Event, JsonElement Payload);

internal sealed class OpenClawWsChannel : IAsyncDisposable
{
    private const int ReceiveBufferSize = 8192;
    private const int ChallengeTimeoutSeconds = 10;
    private const int CloseTimeoutSeconds = 5;
    private const int MaxReconnectAttempts = 5;
    private const int MaxReconnectDelaySeconds = 30;

    private static readonly JsonSerializerOptions WsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _wsUrl;
    private readonly string _gatewayToken;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WsResponse>> _pending = new();
    private readonly Channel<WsEvent> _eventChannel = Channel.CreateUnbounded<WsEvent>();

    private ClientWebSocket _ws;
    private CancellationTokenSource _disposeCts = new();
    private TaskCompletionSource<JsonElement> _challengeTcs;
    private Task _receiveLoop;
    private volatile WsChannelState _state = WsChannelState.Disconnected;
    private int _reconnectAttempt;

    internal OpenClawWsChannel(string wsUrl, string gatewayToken)
    {
        _wsUrl = wsUrl ?? throw new ArgumentNullException(nameof(wsUrl));
        _gatewayToken = gatewayToken ?? throw new ArgumentNullException(nameof(gatewayToken));
    }

    internal WsChannelState State => _state;

    internal async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_state == WsChannelState.Connected) return;
        if (_state == WsChannelState.Disposed) throw new ObjectDisposedException(nameof(OpenClawWsChannel));

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_state == WsChannelState.Connected) return;
            if (_state == WsChannelState.Disposed) throw new ObjectDisposedException(nameof(OpenClawWsChannel));

            _state = WsChannelState.Connecting;

            await ConnectAndHandshakeAsync(ct).ConfigureAwait(false);

            _reconnectAttempt = 0;
            _state = WsChannelState.Connected;
        }
        catch
        {
            _state = WsChannelState.Disconnected;
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    internal async Task<T> SendRequestAsync<T>(string method, object parameters, TimeSpan timeout, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<WsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var frame = new { type = "req", id, method, @params = parameters };
            await SendFrameAsync(frame, ct).ConfigureAwait(false);

            var response = await WaitForResponseAsync(tcs.Task, id, timeout, ct).ConfigureAwait(false);

            if (!response.Ok)
                throw new OpenClawWsException($"WS request '{method}' failed: [{response.ErrorCode}] {response.ErrorMessage}");

            if (response.Payload == null)
                return default;

            return response.Payload.Value.Deserialize<T>(WsJson);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal async Task<WsResponse> SendRequestRawAsync(string method, object parameters, TimeSpan timeout, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<WsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var frame = new { type = "req", id, method, @params = parameters };
            await SendFrameAsync(frame, ct).ConfigureAwait(false);

            return await WaitForResponseAsync(tcs.Task, id, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal async IAsyncEnumerable<WsEvent> SubscribeEventsAsync(string eventPrefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (evt.Event.StartsWith(eventPrefix, StringComparison.OrdinalIgnoreCase))
                yield return evt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_state == WsChannelState.Disposed) return;

        _state = WsChannelState.Disposed;

        _disposeCts.Cancel();

        await CloseSocketGracefullyAsync().ConfigureAwait(false);
        await StopReceiveLoopAsync().ConfigureAwait(false);

        FaultAllPending(new ObjectDisposedException(nameof(OpenClawWsChannel)));
        _eventChannel.Writer.TryComplete();

        _connectLock.Dispose();
        _sendLock.Dispose();
        _disposeCts.Dispose();
        _ws?.Dispose();
    }

    private async Task ConnectAndHandshakeAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        await _ws.ConnectAsync(new Uri(_wsUrl), ct).ConfigureAwait(false);

        _challengeTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_disposeCts.Token), _disposeCts.Token);

        var challengePayload = await WaitForChallengeAsync(ct).ConfigureAwait(false);

        await SendConnectRequestAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> WaitForChallengeAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ChallengeTimeoutSeconds));

        try
        {
            var registration = timeoutCts.Token.Register(() => _challengeTcs.TrySetCanceled(timeoutCts.Token));

            await using (registration.ConfigureAwait(false))
            {
                return await _challengeTcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OpenClawWsException($"No connect.challenge received within {ChallengeTimeoutSeconds}s");
        }
    }

    private async Task SendConnectRequestAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<WsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var connectFrame = new
            {
                type = "req",
                id,
                method = "connect",
                @params = new
                {
                    role = "operator",
                    scopes = new[] { "operator.read", "operator.write" },
                    auth = new { mode = "token", token = _gatewayToken },
                    client = new { id = "squid-gateway-client", version = "1.0" },
                    minProtocol = 1,
                    maxProtocol = 1
                }
            };

            await SendFrameAsync(connectFrame, ct).ConfigureAwait(false);

            var response = await WaitForResponseAsync(tcs.Task, id, TimeSpan.FromSeconds(ChallengeTimeoutSeconds), ct).ConfigureAwait(false);

            if (!response.Ok)
                throw new OpenClawWsException($"Connect rejected: [{response.ErrorCode}] {response.ErrorMessage}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendFrameAsync(object frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, WsJson);
        var segment = new ArraySegment<byte>(bytes);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await _ws.SendAsync(segment, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<WsResponse> WaitForResponseAsync(Task<WsResponse> responseTask, string id, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetCanceled(timeoutCts.Token);
        });

        try
        {
            await using (registration.ConfigureAwait(false))
            {
                return await responseTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"WS request '{id}' timed out after {timeout.TotalSeconds}s");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];

        try
        {
            using var ms = new MemoryStream();

            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Debug("[OpenClaw.WS] Server sent close frame");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                    ProcessFrame(ms.ToArray());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (WebSocketException ex)
        {
            Log.Warning(ex, "[OpenClaw.WS] Receive loop terminated: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw.WS] Unexpected error in receive loop");
        }

        if (_state != WsChannelState.Disposed)
        {
            _ = Task.Run(() => TryReconnectAsync(_disposeCts.Token), _disposeCts.Token);
        }
    }

    private void ProcessFrame(byte[] frameBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(frameBytes);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var frameType = typeProp.GetString();

            switch (frameType)
            {
                case "res":
                    ProcessResponse(root);
                    break;
                case "event":
                    ProcessEvent(root);
                    break;
                default:
                    Log.Debug("[OpenClaw.WS] Unknown frame type: {Type}", frameType);
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "[OpenClaw.WS] Failed to parse frame");
        }
    }

    private void ProcessResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProp)) return;

        var id = idProp.GetString();
        if (id == null || !_pending.TryRemove(id, out var tcs)) return;

        var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

        JsonElement? payload = null;
        if (root.TryGetProperty("payload", out var payloadProp))
            payload = payloadProp.Clone();

        string errorCode = null;
        string errorMessage = null;

        if (!ok && root.TryGetProperty("error", out var errorProp))
        {
            errorCode = errorProp.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
            errorMessage = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : errorProp.GetRawText();
        }

        tcs.TrySetResult(new WsResponse(ok, payload, errorCode, errorMessage));
    }

    private void ProcessEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventProp)) return;

        var eventName = eventProp.GetString();
        if (eventName == null) return;

        JsonElement payload = default;

        if (root.TryGetProperty("payload", out var payloadProp))
            payload = payloadProp.Clone();

        if (eventName == "connect.challenge")
        {
            _challengeTcs?.TrySetResult(payload);
            return;
        }

        _eventChannel.Writer.TryWrite(new WsEvent(eventName, payload));
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        _state = WsChannelState.Disconnected;

        while (_reconnectAttempt < MaxReconnectAttempts && !ct.IsCancellationRequested)
        {
            _reconnectAttempt++;
            var delaySeconds = Math.Min(1 << (_reconnectAttempt - 1), MaxReconnectDelaySeconds);

            Log.Information("[OpenClaw.WS] Reconnect attempt {Attempt}/{Max} in {Delay}s", _reconnectAttempt, MaxReconnectAttempts, delaySeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                await _connectLock.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    if (_state == WsChannelState.Disposed) return;

                    _state = WsChannelState.Connecting;

                    await ConnectAndHandshakeAsync(ct).ConfigureAwait(false);

                    _reconnectAttempt = 0;
                    _state = WsChannelState.Connected;

                    Log.Information("[OpenClaw.WS] Reconnected successfully");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[OpenClaw.WS] Reconnect attempt {Attempt} failed", _reconnectAttempt);
                    _state = WsChannelState.Disconnected;
                }
                finally
                {
                    _connectLock.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        Log.Error("[OpenClaw.WS] Reconnect failed after {Max} attempts", MaxReconnectAttempts);
        FaultAllPending(new OpenClawWsException($"Connection lost after {MaxReconnectAttempts} reconnect attempts"));
    }

    private async Task CloseSocketGracefullyAsync()
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CloseTimeoutSeconds));
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort close — socket may already be broken
        }
    }

    private async Task StopReceiveLoopAsync()
    {
        if (_receiveLoop == null) return;

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[OpenClaw.WS] Receive loop stopped with error during dispose");
        }
    }

    private void FaultAllPending(Exception ex)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }
}

internal class OpenClawWsException : Exception
{
    internal OpenClawWsException(string message) : base(message) { }

    internal OpenClawWsException(string message, Exception inner) : base(message, inner) { }
}
