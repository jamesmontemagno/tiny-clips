using System.IO.Pipes;
using System.Text;
using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public sealed class AutomationPipeServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_CancelsAnActiveRequestEvenWithAnotherRequestQueued(bool useSecureListener)
    {
        Assert.SkipWhen(useSecureListener && !OperatingSystem.IsWindows(), "The listener uses Windows pipe security.");
        var handler = new BlockingHandler();
        await using var server = new AutomationPipeServer(handler.HandleAsync, NewPipeName());

        var (client, serving) = await ConnectAsync(server, useSecureListener);
        await using (client)
        {
            await client.WriteAsync("first\nsecond\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await client.FlushAsync(TestContext.Current.CancellationToken);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Handled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectedClient_ReceivesPipelinedResponsesInOrder(bool useSecureListener)
    {
        Assert.SkipWhen(useSecureListener && !OperatingSystem.IsWindows(), "The listener uses Windows pipe security.");
        await using var server = new AutomationPipeServer(
            (line, _) => Task.FromResult($"response:{line}"), NewPipeName());
        var (client, serving) = await ConnectAsync(server, useSecureListener);
        await using (client)
        {
            await client.WriteAsync("first\nsecond\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await client.FlushAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

            Assert.Equal("response:first", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
            Assert.Equal("response:second", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        }
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TooManyQueuedRequests_CancelsTheActiveRequestAndClosesTheConnection()
    {
        var handler = new BlockingHandler();
        await using var server = new AutomationPipeServer(handler.HandleAsync, NewPipeName());
        var (client, serving) = await ConnectAsync(server, useSecureListener: false);
        await using (client)
        {
            await client.WriteAsync("active\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await client.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("queued\n", 17))),
                TestContext.Current.CancellationToken);

            await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, handler.Handled);
    }

    private sealed class BlockingHandler
    {
        private int _handled;
        public int Handled => Volatile.Read(ref _handled);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> HandleAsync(string _, CancellationToken token)
        {
            Interlocked.Increment(ref _handled);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unexpected";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    private static string NewPipeName() => "tc" + Guid.NewGuid().ToString("N")[..16];

    private static async Task<(NamedPipeClientStream Client, Task Serving)> ConnectAsync(
        AutomationPipeServer server, bool useSecureListener)
    {
        var token = TestContext.Current.CancellationToken;
        var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        NamedPipeServerStream? pipe = null;
        try
        {
            if (useSecureListener)
            {
                server.Start();
                await client.ConnectAsync(token);
                return (client, Task.CompletedTask);
            }

            pipe = new NamedPipeServerStream(server.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var connecting = pipe.WaitForConnectionAsync(token);
            await client.ConnectAsync(token);
            await connecting;
            return (client, server.ServeAsync(pipe, token));
        }
        catch
        {
            await client.DisposeAsync();
            if (pipe is not null) await pipe.DisposeAsync();
            throw;
        }
    }
}
