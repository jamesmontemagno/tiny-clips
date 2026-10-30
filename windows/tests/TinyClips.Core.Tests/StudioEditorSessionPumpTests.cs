namespace TinyClips.Core.Tests;

public sealed class StudioEditorSessionPumpTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task CompletionWithoutAnotherPost_WakesThePump()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumping = PumpUntilCompletedAsync(completion.Task);
        Assert.False(pumping.IsCompleted);

        completion.SetResult();

        await pumping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailureWithoutAnotherPost_IsPropagated()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumping = PumpUntilCompletedAsync(completion.Task);
        var failure = new InvalidOperationException("The operation failed after its final post.");

        completion.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pumping));
    }

    [Fact]
    public async Task AlreadyCompletedOperation_DoesNotWaitForAPost()
    {
        await PumpUntilCompletedAsync(Task.CompletedTask);
    }
}
