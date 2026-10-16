using Vortice.Direct3D11;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// What a <see cref="StudioPreviewEngine"/> draws into: a swap chain shown in a window, or a plain
/// texture. The engine attaches at most one surface at a time and works without one.
/// </summary>
/// <remarks>
/// <para>
/// For every frame the engine calls, on its render thread: <see cref="Configure"/> if the surface
/// asks for it, then <see cref="AcquireTarget"/>, then it draws, then <see cref="Present"/>. It
/// holds its device lock from <see cref="Configure"/> until the drawing is done and has released
/// it before <see cref="Present"/>. The engine never makes two of these calls at once, so a
/// surface needs no lock of its own for what only they touch.
/// </para>
/// <para>
/// <see cref="ReleaseDeviceResources"/> comes from whichever thread detaches the surface or
/// disposes the engine. <see cref="NeedsConfigure"/> and <see cref="Invalidated"/> are the
/// surface's side: it uses them from any thread, typically the UI thread.
/// </para>
/// <para>
/// An implementation must not wait for another thread in any member (the engine may be called by
/// that thread at the same moment), and must not call the engine from inside one.
/// </para>
/// </remarks>
public interface IStudioPreviewSurface
{
    /// <summary>
    /// Raised when the surface's size or scale has changed, or when it wants the current frame
    /// drawn again. The engine answers with a new frame; nothing is drawn or resized inside the
    /// handler.
    /// </summary>
    event EventHandler? Invalidated;

    /// <summary>
    /// True while the surface's buffers are missing or no longer have the size it wants. The
    /// engine reads it before every frame.
    /// </summary>
    bool NeedsConfigure { get; }

    /// <summary>
    /// Creates the buffers on <paramref name="device"/>, resizes them to the size the surface
    /// wants now, or releases them when that size is empty. Called with the device lock held and
    /// after the engine has dropped every reference to a texture from <see cref="AcquireTarget"/>,
    /// which a swap chain needs before it can resize its buffers. Buffers are never resized
    /// anywhere else, so the UI thread never waits for presents that are still queued.
    /// </summary>
    void Configure(ID3D11Device device);

    /// <summary>
    /// The texture the next frame is drawn into and its size in pixels, or null when there is
    /// nothing to draw into. The texture must be a BGRA render target on the device given to
    /// <see cref="Configure"/>. The engine owns the returned reference and keeps it until the next
    /// <see cref="Configure"/> or <see cref="ReleaseDeviceResources"/>. Called with the device
    /// lock held.
    /// </summary>
    ID3D11Texture2D? AcquireTarget(out int pixelWidth, out int pixelHeight);

    /// <summary>
    /// Shows what was drawn into the acquired target. Called once per drawn frame, without the
    /// device lock, and only when the engine has drawn something new.
    /// </summary>
    void Present();

    /// <summary>
    /// Releases everything created on the device. Called with the device lock held and no
    /// reference to a target outstanding: when the surface is detached, when the engine is
    /// disposed, and before a lost device is replaced (<see cref="Configure"/> then follows with
    /// the new one).
    /// </summary>
    void ReleaseDeviceResources();
}
