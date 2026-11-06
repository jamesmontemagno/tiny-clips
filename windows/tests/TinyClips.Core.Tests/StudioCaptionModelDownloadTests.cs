using System.Diagnostics;
using System.Net;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Tests;

/// <summary>
/// The real download path, without the network. The app's one HttpClient keeps its timeout of 100
/// seconds, and a large model takes far longer than that on most connections.
/// </summary>
public sealed class StudioCaptionModelDownloadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("TinyClipsModels").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Takes longer to give its bytes than the client allows for a request.</summary>
    private sealed class SlowStream(int chunks, TimeSpan delay) : Stream
    {
        private int _left = chunks;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left-- <= 0)
            {
                return 0;
            }

            await Task.Delay(delay, cancellationToken);
            buffer.Span[0] = 42;
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Server(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(respond());
        }
    }

    [Fact]
    public async Task ADownloadSlowerThanTheClientsTimeout_IsNotCutOff()
    {
        var server = new Server(static () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SlowStream(chunks: 6, TimeSpan.FromMilliseconds(100))),
        });
        using var client = new HttpClient(server) { Timeout = TimeSpan.FromMilliseconds(250) };
        var store = new StudioCaptionModelStore(_directory, client);
        var model = StudioCaptionModels.Default;

        var clock = Stopwatch.StartNew();
        var path = await store.EnsureAsync(model, progress: null, TestContext.Current.CancellationToken);

        Assert.True(clock.Elapsed > client.Timeout, "the download finished before the timeout could matter");
        Assert.Equal(6, new FileInfo(path).Length);
        Assert.Equal("huggingface.co", server.Asked!.Host);
    }

    [Fact]
    public async Task AServerThatSaysNo_LeavesNothingBehind()
    {
        var server = new Server(static () => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(server);
        var store = new StudioCaptionModelStore(_directory, client);
        var model = StudioCaptionModels.Default;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            store.EnsureAsync(model, progress: null, TestContext.Current.CancellationToken));

        Assert.False(store.IsInstalled(model));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task ADownloadThatDiesPartWay_LeavesNothingBehind()
    {
        var server = new Server(static () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DyingStream()),
        });
        using var client = new HttpClient(server);
        var store = new StudioCaptionModelStore(_directory, client);
        var model = StudioCaptionModels.Default;

        await Assert.ThrowsAsync<IOException>(() =>
            store.EnsureAsync(model, progress: null, TestContext.Current.CancellationToken));

        Assert.False(store.IsInstalled(model));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    private sealed class DyingStream : MemoryStream
    {
        private bool _gaveSome;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_gaveSome)
            {
                throw new IOException("The connection was lost.");
            }

            _gaveSome = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
