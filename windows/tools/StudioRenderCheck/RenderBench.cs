using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// A device, a renderer and two source pictures, for drawing single frames and reading them back.
/// The pictures are the test clips' pattern, so what a frame shows can be read from its pixels.
/// </summary>
internal sealed class RenderBench : IDisposable
{
    public const int ScreenNumber = 0xA53;

    /// <summary>Not the same read backwards, so a mirrored strip never reads as this.</summary>
    public const int CameraNumber = 0x5AC;

    private readonly Dictionary<(int Width, int Height), ID3D11Texture2D> _targets = [];
    private readonly List<ID3D11Texture2D> _textures = [];

    public RenderBench(bool warp)
    {
        Graphics = warp ? StudioGraphicsDevice.CreateWarp() : StudioGraphicsDevice.CreateHardware();
        try
        {
            Renderer = new StudioSceneRenderer(Graphics.Device);
            ScreenFrame = new StudioGpuVideoFrame(Texture(ScreenClip.Spec.DrawBgra(ScreenNumber), 1920, 1080), 0, 1920, 1080);
            CameraFrame = new StudioGpuVideoFrame(Texture(CameraClip.Spec.DrawBgra(CameraNumber), 1280, 720), 0, 1280, 720);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public StudioGraphicsDevice Graphics { get; }

    public StudioSceneRenderer Renderer { get; } = null!;

    public TestClip ScreenClip { get; } = new("screen.mp4", ClipSpec.Screen(1920, 1080), 30, 1, 180, 0);

    public TestClip CameraClip { get; } = new("camera.mp4", ClipSpec.Camera(1280, 720), 30, 1, 180, 0);

    public StudioGpuVideoFrame ScreenFrame { get; }

    public StudioGpuVideoFrame CameraFrame { get; }

    /// <summary>A folder with nothing in it, for projects that have no background image.</summary>
    public string EmptyFolder { get; } = Path.GetTempPath();

    /// <summary>
    /// The standard project with everything soft switched off, so a pixel is exactly one colour:
    /// solid orange background, no shadows, no border, circular camera bubble, mirrored.
    /// </summary>
    public StudioProject Base(StudioLayout layout = StudioLayout.Bubble) =>
        Projects.Create(ScreenClip, CameraClip, cameraOffset: 0) with
        {
            Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = RendererChecks.Orange } },
            Screen = new StudioScreenStyle { Shadow = 0 },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = true, Shadow = 0, BorderWidth = 0 },
            Scenes = [new StudioScene { Layout = layout, Bubble = new StudioBubble { Size = 0.36 } }],
        };

    /// <summary>Draws one frame and reads it back.</summary>
    public Picture Render(
        StudioProject project,
        int width = 1920,
        int height = 1080,
        double time = 0,
        StudioEvents? events = null,
        bool withScreen = true,
        bool withCamera = true,
        StudioRenderQuality quality = StudioRenderQuality.Preview,
        string? folder = null,
        StudioGpuVideoFrame? screen = null,
        StudioGpuVideoFrame? camera = null,
        StudioSceneRenderer? renderer = null)
    {
        var target = Target(width, height);
        lock (Graphics.Gate)
        {
            (renderer ?? Renderer).Render(new StudioRenderRequest(
                project,
                events ?? Projects.Events(ScreenClip),
                folder ?? EmptyFolder,
                time,
                width,
                height,
                withScreen ? screen ?? ScreenFrame : null,
                withCamera ? camera ?? CameraFrame : null,
                target,
                null,
                quality));
            return Picture.FromBgra(Graphics.ReadTexture(target), width, height);
        }
    }

    public ID3D11Texture2D Target(int width, int height)
    {
        if (!_targets.TryGetValue((width, height), out var target))
        {
            target = Graphics.CreateRenderTexture(width, height);
            _targets[(width, height)] = target;
        }

        return target;
    }

    /// <summary>
    /// A texture holding a tightly packed BGRA picture at its top-left corner. A texture larger
    /// than the picture is filled with magenta around it, which shows at once if it leaks in.
    /// </summary>
    public unsafe ID3D11Texture2D Texture(byte[] bgra, int width, int height, int textureWidth = 0, int textureHeight = 0, Format format = Format.B8G8R8A8_UNorm)
    {
        textureWidth = Math.Max(width, textureWidth);
        textureHeight = Math.Max(height, textureHeight);
        var pixels = bgra;
        if (textureWidth != width || textureHeight != height)
        {
            pixels = new byte[textureWidth * textureHeight * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 1] = 0;
                pixels[i + 2] = 255;
                pixels[i + 3] = 255;
            }

            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(bgra, row * width * 4, pixels, row * textureWidth * 4, width * 4);
            }
        }

        var texture = Graphics.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)textureWidth,
            Height = (uint)textureHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _textures.Add(texture);
        fixed (byte* source = pixels)
        {
            lock (Graphics.Gate)
            {
                Graphics.Context.UpdateSubresource(texture, 0, null, (nint)source, (uint)(textureWidth * 4), 0);
            }
        }

        return texture;
    }

    public void Dispose()
    {
        lock (Graphics.Gate)
        {
            Renderer?.Dispose();
        }

        foreach (var texture in _textures)
        {
            texture.Dispose();
        }

        foreach (var target in _targets.Values)
        {
            target.Dispose();
        }

        Graphics.Dispose();
    }
}
