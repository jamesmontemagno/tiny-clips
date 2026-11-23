namespace TinyClips.Core.Services.ClipsLibrary;

public static class ClipDeleteConfirmation
{
    public static Task<bool> ApproveAsync(bool confirmDelete, Func<Task<bool>> confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(confirmAsync);
        return confirmDelete ? confirmAsync() : Task.FromResult(true);
    }
}
