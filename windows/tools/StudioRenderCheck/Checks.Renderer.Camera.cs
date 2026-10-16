using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Camera shapes, mirror, crops, scenes, visibility and the border.</summary>
internal static partial class RendererChecks
{
    /// <summary>The half-diagonal of a unit square at which a superellipse of exponent 5 ends: 2^(-1/5).</summary>
    internal const double SquircleDiagonal = 0.8705505632961241;

    private static StudioProject WithCamera(RenderBench bench, StudioCameraShape shape, double border = 0, string borderColor = "#FFFFFF", bool mirror = true, double shadow = 0) =>
        bench.Base() with { Camera = new StudioCameraStyle { Shape = shape, CornerRadius = 0.3, Mirror = mirror, Shadow = shadow, BorderWidth = border, BorderColor = borderColor } };

    /// <summary>Pairs of points 4 px inside and 4 px outside the outline of a shape, all the way round.</summary>
    internal static List<((double X, double Y) Inside, (double X, double Y) Outside)> OutlinePairs(StudioCameraShape shape, (int Left, int Top, int Right, int Bottom) box, double radius)
    {
        var pairs = new List<((double, double), (double, double))>();
        var a = (box.Right - box.Left) / 2.0;
        var b = (box.Bottom - box.Top) / 2.0;
        var cx = box.Left + a;
        var cy = box.Top + b;
        if (shape is StudioCameraShape.Circle or StudioCameraShape.Squircle)
        {
            for (var index = 0; index < 24; index++)
            {
                var theta = (Math.PI * 2 * index / 24) + 0.13;
                var cos = Math.Cos(theta);
                var sin = Math.Sin(theta);
                var (ux, uy) = shape == StudioCameraShape.Circle
                    ? (a * cos, b * sin)
                    : (a * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 0.4), b * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 0.4));
                var length = Math.Sqrt((ux * ux) + (uy * uy));
                pairs.Add(((cx + (ux * (1 - (4 / length))), cy + (uy * (1 - (4 / length)))), (cx + (ux * (1 + (4 / length))), cy + (uy * (1 + (4 / length))))));
            }

            return pairs;
        }

        var middleX = (box.Left + box.Right) / 2.0;
        var middleY = (box.Top + box.Bottom) / 2.0;
        pairs.Add(((box.Left + 4, middleY), (box.Left - 4, middleY)));
        pairs.Add(((box.Right - 4, middleY), (box.Right + 4, middleY)));
        pairs.Add(((middleX, box.Top + 4), (middleX, box.Top - 4)));
        pairs.Add(((middleX, box.Bottom - 4), (middleX, box.Bottom + 4)));
        var diagonal = Math.Sqrt(0.5);
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            var cornerX = sx < 0 ? box.Left : box.Right;
            var cornerY = sy < 0 ? box.Top : box.Bottom;
            if (shape == StudioCameraShape.Rectangle || radius <= 0)
            {
                pairs.Add(((cornerX - (sx * 3), cornerY - (sy * 3)), (cornerX + (sx * 3), cornerY + (sy * 3))));
            }
            else
            {
                var centerX = cornerX - (sx * radius);
                var centerY = cornerY - (sy * radius);
                pairs.Add(((centerX + (sx * (radius - 4) * diagonal), centerY + (sy * (radius - 4) * diagonal)), (centerX + (sx * (radius + 4) * diagonal), centerY + (sy * (radius + 4) * diagonal))));
            }
        }

        return pairs;
    }

    private static void Shape(CheckContext context, RenderBench bench, StudioCameraShape shape, (int Left, int Top, int Right, int Bottom) want)
    {
        var project = WithCamera(bench, shape);
        var resolved = StudioLayoutResolver.Resolve(project, 0, 1920, 1080);
        var camera = resolved.Camera!.Value;
        ExpectBox(context, "the bubble", camera.Rect, want);
        context.Expect(camera.Shape == shape, $"the bubble resolves to {camera.Shape}");

        // Half the bubble for the round shapes, 0.3 of its short side for the rounded rectangle, none for the rectangle.
        var wantRadius = shape switch
        {
            StudioCameraShape.Circle or StudioCameraShape.Squircle => 194.4,
            StudioCameraShape.RoundedRectangle => 116.64,
            _ => 0,
        };
        context.Expect(Math.Abs(camera.CornerRadius - wantRadius) < 1e-9, $"the bubble's corner radius resolves to {camera.CornerRadius}, want {wantRadius}");

        var picture = bench.Render(project);
        var without = bench.Render(project, withCamera: false);
        var box = Box(camera.Rect);
        var map = Projects.CameraMap(camera, bench.CameraClip.Spec);
        ExpectContent(context, "the camera", picture, bench.CameraClip.Spec, map, RenderBench.CameraNumber);

        var pairs = OutlinePairs(shape, box, camera.CornerRadius);
        var a = (box.Right - box.Left) / 2.0;
        var b = (box.Bottom - box.Top) / 2.0;
        var cx = box.Left + a;
        var cy = box.Top + b;

        // Points that tell the shapes apart on the diagonal: a circle ends at 0.707 of the way to
        // the corner of its box, a squircle at 0.871, a rectangle at the corner.
        (double X, double Y) Diagonal(double fraction) => (cx + (a * fraction), cy - (b * fraction));
        switch (shape)
        {
            case StudioCameraShape.Circle:
                pairs.Add((Diagonal(0.68), Diagonal(0.74)));
                break;
            case StudioCameraShape.Squircle:
                pairs.Add((Diagonal(0.80), Diagonal(0.94)));
                pairs.Add((Diagonal(SquircleDiagonal - 0.03), Diagonal(SquircleDiagonal + 0.03)));
                break;
            case StudioCameraShape.Rectangle:
                pairs.Add((Diagonal(0.97), Diagonal(1.03)));
                break;
        }

        var matched = 0;
        foreach (var (inside, outside) in pairs)
        {
            ExpectPixel(context, picture, outside.X, outside.Y, without.Pixel((int)Math.Floor(outside.X), (int)Math.Floor(outside.Y)), 0, $"outside the {shape} at ({outside.X:0},{outside.Y:0})");
            var got = picture.Pixel((int)Math.Floor(inside.X), (int)Math.Floor(inside.Y));
            context.Expect(got.Distance(without.Pixel((int)Math.Floor(inside.X), (int)Math.Floor(inside.Y))) > 20, $"inside the {shape} at ({inside.X:0},{inside.Y:0}) the camera is not drawn: {got}");
            if (ClipColorAt(bench.CameraClip.Spec, map, RenderBench.CameraNumber, inside.X, inside.Y) is { } flat)
            {
                matched++;
                context.Expect(got.Distance(flat) <= 1, $"inside the {shape} at ({inside.X:0},{inside.Y:0}): {got}, and the camera has {flat} there");
            }
        }

        context.Expect(matched >= pairs.Count / 2, $"only {matched} of {pairs.Count} inside points could be compared with the pattern");
        context.Note($"{pairs.Count} points 4 px inside and outside the outline; {matched} inside points compared with the camera's own colour");
    }

    private static void Mirror(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        foreach (var mirror in new[] { true, false })
        {
            var project = WithCamera(bench, StudioCameraShape.Circle, mirror: mirror);
            var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
            context.Expect(camera.Mirror == mirror, $"mirror resolves to {camera.Mirror}");
            var picture = bench.Render(project);
            var number = FrameCode.Decode(picture, spec, Projects.CameraMap(camera, spec));
            var flipped = FrameCode.Decode(picture, spec, Projects.CameraMap(camera with { Mirror = !mirror }, spec));
            context.Expect(number == RenderBench.CameraNumber, $"mirror {mirror}: the strip reads {number}, want {RenderBench.CameraNumber}");
            context.Expect(flipped != RenderBench.CameraNumber, $"mirror {mirror}: the strip also reads right the other way round");

            // The camera's left third is darker. A quarter of the way down and 15 % in from the
            // bubble's left edge is that third when the picture is not mirrored.
            var box = Box(camera.Rect);
            var x = box.Left + (0.15 * (box.Right - box.Left));
            var y = box.Top + (0.25 * (box.Bottom - box.Top));
            ExpectPixel(context, picture, x, y, mirror ? spec.Background : spec.LeftBackground, 1, $"mirror {mirror}: the left of the bubble");
        }
    }

    private static void CropsAndScenes(CheckContext context, RenderBench bench)
    {
        // The top-left quarter of the screen, and the middle half of the camera's width.
        var cropped = bench.Base() with
        {
            Screen = new StudioScreenStyle { Shadow = 0, Crop = new StudioRect(0, 0, 0.5, 0.5) },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = false, Shadow = 0, Crop = new StudioRect(0.25, 0, 0.5, 1) },
        };
        var resolved = StudioLayoutResolver.Resolve(cropped, 0, 1920, 1080);
        var screen = resolved.Screen!.Value;
        var camera = resolved.Camera!.Value;
        context.Expect(screen.Source == new StudioFrameRect(0, 0, 0.5, 0.5), $"the screen's source rect is {screen.Source}");

        // A 640×720 crop filling a square: its full width, and 640 of its 720 rows.
        var wantCamera = new StudioFrameRect(0.25, 40.0 / 720, 0.5, 640.0 / 720);
        context.Expect(Math.Abs(camera.Source.X - wantCamera.X) < 1e-9 && Math.Abs(camera.Source.Y - wantCamera.Y) < 1e-9 && Math.Abs(camera.Source.Width - wantCamera.Width) < 1e-9 && Math.Abs(camera.Source.Height - wantCamera.Height) < 1e-9, $"the camera's source rect is {camera.Source}, want {wantCamera}");
        var picture = bench.Render(cropped);
        var screenMap = Projects.ScreenMap(screen, bench.ScreenClip.Spec);
        ExpectContent(context, "the cropped screen", picture, bench.ScreenClip.Spec, screenMap, RenderBench.ScreenNumber);
        ExpectContent(context, "the cropped camera", picture, bench.CameraClip.Spec, Projects.CameraMap(camera, bench.CameraClip.Spec), RenderBench.CameraNumber);

        // The crop's corner is the card's corner: 2× the scale of the uncropped card.
        context.Expect(Math.Abs(screenMap.ScaleX - (1690.0 / 960)) < 1e-9, $"the cropped screen is drawn at {screenMap.ScaleX}× its pixels");

        // Two scenes: the bubble until 2 s, then the camera alone.
        var scenes = bench.Base() with { Scenes = [new StudioScene { Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.36 } }, new StudioScene { Start = 2, Layout = StudioLayout.Camera }] };
        var early = bench.Render(scenes, time: 1.999);
        var late = bench.Render(scenes, time: 2);
        ExpectSame(context, early, bench.Render(bench.Base(StudioLayout.Bubble)), "just before the second scene");
        ExpectSame(context, late, bench.Render(bench.Base(StudioLayout.Camera)), "at the start of the second scene");
    }

    private static void InvisibleCamera(CheckContext context, RenderBench bench)
    {
        // The camera runs from 2 s to 3 s of source time, with a shadow and a border that must not show either.
        var project = WithCamera(bench, StudioCameraShape.Circle, border: 0.01, shadow: 1);
        project = project with { Sources = project.Sources with { Camera = project.Sources.Camera! with { StartOffset = 2, Duration = 1 } } };
        foreach (var (time, visible) in new[] { (1.0, false), (1.999, false), (2.0, true), (2.5, true), (3.0, true), (3.001, false), (5.0, false) })
        {
            var resolved = StudioLayoutResolver.Resolve(project, time, 1920, 1080).Camera!.Value;
            context.Expect(resolved.Visible == visible, $"at {time} s the layout says visible = {resolved.Visible}");
            var picture = bench.Render(project, time: time);
            var without = bench.Render(project, time: time, withCamera: false);
            var (difference, _, _) = Picture.MaxDifference(picture, without);
            if (visible)
            {
                context.Expect(difference > 50, $"at {time} s the camera is not drawn");
            }
            else
            {
                context.Expect(difference == 0, $"at {time} s, with the camera not visible, {difference} of 255 of it is drawn");
            }
        }
    }

    private static void Border(CheckContext context, RenderBench bench)
    {
        var borderColor = new Rgb(255, 128, 0);

        // 0.01 of the short side: a 10.8 px stroke inside the shape.
        foreach (var shape in new[] { StudioCameraShape.Circle, StudioCameraShape.Squircle, StudioCameraShape.RoundedRectangle, StudioCameraShape.Rectangle })
        {
            var project = WithCamera(bench, shape, border: 0.01, borderColor: "#FF8000");
            var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
            context.Expect(Math.Abs(camera.BorderWidth - 10.8) < 1e-9, $"the border resolves to {camera.BorderWidth} px, want 10.8");
            var picture = bench.Render(project);
            var plain = bench.Render(WithCamera(bench, shape));
            var without = bench.Render(project, withCamera: false);
            var box = Box(camera.Rect);
            var middleX = (box.Left + box.Right) / 2;
            var middleY = (box.Top + box.Bottom) / 2;
            foreach (var (name, x, y, dx, dy) in new[] { ("left", box.Left, middleY, 1, 0), ("right", box.Right - 1, middleY, -1, 0), ("top", middleX, box.Top, 0, 1), ("bottom", middleX, box.Bottom - 1, 0, -1) })
            {
                ExpectPixel(context, picture, x - (2 * dx), y - (2 * dy), without.Pixel(x - (2 * dx), y - (2 * dy)), 0, $"{shape}: 2 px outside the {name} edge");
                ExpectPixel(context, picture, x + dx, y + dy, borderColor, 1, $"{shape}: 1 px inside the {name} edge");
                ExpectPixel(context, picture, x + (9 * dx), y + (9 * dy), borderColor, 1, $"{shape}: 9 px inside the {name} edge");

                // Past the stroke the camera shows, as it does with no border.
                ExpectPixel(context, picture, x + (13 * dx), y + (13 * dy), plain.Pixel(x + (13 * dx), y + (13 * dy)), 0, $"{shape}: 13 px inside the {name} edge");
            }

            // Away from the stroke nothing changes: the middle 40 % of the bubble each way.
            var insetX = (int)(0.3 * (box.Right - box.Left));
            var insetY = (int)(0.3 * (box.Bottom - box.Top));
            ExpectSame(context, picture, plain, $"{shape}: the middle of the bubble with and without a border", box.Left + insetX, box.Top + insetY, box.Right - insetX, box.Bottom - insetY);

            if (shape is StudioCameraShape.Circle or StudioCameraShape.Squircle)
            {
                // On the diagonal: how far inside the outline the stroke begins, and how wide it is.
                var a = (box.Right - box.Left) / 2.0;
                var fraction = shape == StudioCameraShape.Circle ? Math.Sqrt(0.5) : SquircleDiagonal;
                var edge = a * fraction * Math.Sqrt(2);
                double? begins = null;
                double? ends = null;
                for (var inward = -3.0; inward < 30; inward += 0.25)
                {
                    var reach = (edge - inward) / Math.Sqrt(2);
                    var pixel = picture.Pixel((int)Math.Floor(box.Left + a + reach), (int)Math.Floor(box.Top + a - reach));
                    if (pixel.Distance(borderColor) <= 1)
                    {
                        begins ??= inward;
                        ends = inward;
                    }
                }

                context.Expect(begins is not null, $"{shape}: no border on the diagonal");
                if (begins is { } first && ends is { } last)
                {
                    context.Expect(first <= (shape == StudioCameraShape.Circle ? 1.5 : 3), $"{shape}: on the diagonal the border begins {Show(first)} px inside the outline");
                    context.Note($"{shape}: on the diagonal the border is solid from {Show(first)} to {Show(last)} px inside the outline");
                }
            }
        }

        // A border thinner than a pixel is drawn one pixel wide. On a rectangle that is exactly the edge row.
        var thin = WithCamera(bench, StudioCameraShape.Rectangle, border: 0.0004, borderColor: "#FF8000");
        var thinCamera = StudioLayoutResolver.Resolve(thin, 0, 1920, 1080).Camera!.Value;
        context.Expect(thinCamera.BorderWidth is > 0.4 and < 0.5, $"the thin border resolves to {thinCamera.BorderWidth} px");
        var thinPicture = bench.Render(thin);
        var thinPlain = bench.Render(WithCamera(bench, StudioCameraShape.Rectangle));
        var thinBox = Box(thinCamera.Rect);
        var row = (thinBox.Top + thinBox.Bottom) / 2;
        ExpectPixel(context, thinPicture, thinBox.Left, row, borderColor, 1, "a 0.43 px border: the edge pixel");
        ExpectPixel(context, thinPicture, thinBox.Left + 1, row, thinPlain.Pixel(thinBox.Left + 1, row), 0, "a 0.43 px border: the pixel next to the edge");
        ExpectPixel(context, thinPicture, thinBox.Right - 1, row, borderColor, 1, "a 0.43 px border: the right edge pixel");
        ExpectPixel(context, thinPicture, thinBox.Right - 2, row, thinPlain.Pixel(thinBox.Right - 2, row), 0, "a 0.43 px border: the pixel next to the right edge");

        // A colour with alpha is blended over the camera: #FF800080 is 128/255 of orange.
        var faint = bench.Render(WithCamera(bench, StudioCameraShape.Rectangle, border: 0.01, borderColor: "#FF800080"));
        var under = thinPlain.Pixel(thinBox.Left + 4, row);
        ExpectPixel(context, faint, thinBox.Left + 4, row, under.Mix(borderColor, 128 / 255.0), 1.5, "a half-transparent border over the camera");
    }
}
