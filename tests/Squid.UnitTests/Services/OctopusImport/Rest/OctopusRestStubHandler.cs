using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// In-memory Octopus server: answers each GET from a route function and records what was asked. <see cref="Latency"/>
/// makes every answer take that long (per request, so concurrent requests overlap), and <see cref="MaxInFlight"/> is
/// the most requests it was answering at once.
/// </summary>
internal sealed class OctopusRestStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private int _inFlight;
    private int _maxInFlight;

    public List<(Uri Uri, string ApiKey)> Requests { get; } = [];

    public IEnumerable<string> PathsAndQueries
    {
        get
        {
            lock (Requests) return Requests.Select(r => r.Uri.PathAndQuery).ToList();
        }
    }

    /// <summary>How long each request takes; a function so a test can vary it by request (scrambling completion order).</summary>
    public Func<HttpRequestMessage, TimeSpan> Latency { get; set; }

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = request.Headers.TryGetValues(OctopusRestClient.ApiKeyHeader, out var values) ? values.Single() : null;

        lock (Requests) Requests.Add((request.RequestUri, apiKey));

        TrackMax(Interlocked.Increment(ref _inFlight));

        try
        {
            if (Latency?.Invoke(request) is { } latency && latency > TimeSpan.Zero) await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

            return respond(request);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void TrackMax(int inFlight)
    {
        var max = Volatile.Read(ref _maxInFlight);

        while (inFlight > max && Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max) max = Volatile.Read(ref _maxInFlight);
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    public static object Root(bool spaces = true)
        => new { Application = "Octopus Deploy", Version = "2023.4.1", InstallationId = "inst-1", Links = spaces ? new Dictionary<string, string> { ["Spaces"] = "/api/spaces{/id}" } : new Dictionary<string, string>() };

    public static object Page<T>(IReadOnlyCollection<T> items, int total) => new { Items = items, TotalResults = total, ItemsPerPage = 100 };

    /// <summary>A 200 whose body sends its first bytes and then never sends another one.</summary>
    public static HttpResponseMessage StalledBody() => new(HttpStatusCode.OK) { Content = new StreamContent(new BodyStream(stall: true)) };

    /// <summary>A 200 whose connection resets part way through the body.</summary>
    public static HttpResponseMessage ResetBody() => new(HttpStatusCode.OK) { Content = new StreamContent(new BodyStream(stall: false)) };

    private sealed class BodyStream(bool stall) : Stream
    {
        private bool _sentFirstBytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirstBytes)
            {
                _sentFirstBytes = true;
                buffer.Span[0] = (byte)'{';
                return 1;
            }

            if (!stall) throw new IOException("connection reset by octopus.example.com");

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
