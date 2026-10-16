using System.Numerics;
using AgentIsland.WinUI.Shell;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace AgentIsland.WinUI.Drawer;

/// Draws one bot in its pose during a drawer moment, for the drawer's loop
/// and the debug film alike. Pixel bots move in whole pixels, unfiltered,
/// with a hard shadow; Classic bots lean and squash about their base over a
/// soft floor shadow, drawn as vectors while scaled so they stay sharp.
internal static class BotPainter
{
    public const float Size = 44;

    /// Where Classic bots lean and squash: 88 % down the box, their base.
    public static readonly Vector2 Base = new(Size / 2, Size * 0.88f);

    private static ColorMatrixEffect? _silhouette;

    /// A rest pose is drawn exactly as the drawer draws it between moments.
    /// `circle` clips a pixel bot to its circle wherever it is drawn, as the
    /// window region clips it at rest, so nothing it paints past the circle
    /// appears during a moment and vanishes when the region comes back.
    public static void Draw(CanvasDrawingSession ds, CanvasBitmap frame, string svg, SvgCanvas vectors,
        float x, float y, BotPose pose, bool pixel, bool dark, float edge, float scale, bool circle = false)
    {
        var at = new Vector2(x + pose.X * edge, y + pose.Y);
        if (pixel)
        {
            var spot = Snap(at, scale);
            if (pose.Shadow > 0)
            {
                // The silhouette in the kit's shadow colour, a cell back
                // toward the edge and a cell down.
                var offset = new Vector2(2 * edge, 2);
                var shadow = Silhouette(frame, PixelTokens.Get("Shadow", dark));
                using (Circle(ds, circle, spot + offset, scale))
                {
                    ds.DrawImage(shadow, spot + offset, frame.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
                }
            }
            using (Circle(ds, circle, spot, scale))
            {
                ds.DrawImage(frame, spot, frame.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
            }
            return;
        }
        if (pose.Shadow > 0) Floor(ds, at + new Vector2(Size / 2, 41 + pose.ShadowY), pose.Shadow, dark);
        if (pose.ScaleX == 1 && pose.ScaleY == 1 && pose.Lean == 0)
        {
            ds.DrawImage(frame, at.X, at.Y, frame.Bounds, pose.Opacity);
            return;
        }
        if (Math.Abs(pose.ScaleX) < 0.01f || Math.Abs(pose.ScaleY) < 0.01f || pose.Opacity <= 0) return;
        var origin = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-Base)
            * Matrix3x2.CreateScale(pose.ScaleX, pose.ScaleY)
            * Matrix3x2.CreateRotation(pose.Lean * edge * MathF.PI / 180)
            * Matrix3x2.CreateTranslation(Base + at)
            * origin;
        if (pose.Opacity < 1)
        {
            using (ds.CreateLayer(pose.Opacity)) vectors.Render(svg, ds);
        }
        else vectors.Render(svg, ds);
        ds.Transform = origin;
    }

    /// The pose's transform for things drawn without a frame (the fallback
    /// discs): Pixel moves only; Classic also leans and squashes.
    public static Matrix3x2 Transform(float x, float y, BotPose pose, bool pixel, float edge, float scale)
    {
        var at = new Vector2(x + pose.X * edge, y + pose.Y);
        if (pixel) return Matrix3x2.CreateTranslation(Snap(at, scale));
        return Matrix3x2.CreateTranslation(-Base)
            * Matrix3x2.CreateScale(pose.ScaleX, pose.ScaleY)
            * Matrix3x2.CreateRotation(pose.Lean * edge * MathF.PI / 180)
            * Matrix3x2.CreateTranslation(Base + at);
    }

    /// The window region's circle for a bot drawn at `at`: GDI's ellipse
    /// over the box's whole pixels, one more pixel right and down.
    private static IDisposable? Circle(CanvasDrawingSession ds, bool on, Vector2 at, float scale)
    {
        if (!on) return null;
        var radius = Size / 2 + 0.5f / scale;
        using var geometry = CanvasGeometry.CreateEllipse(ds, at + new Vector2(radius, radius), radius, radius);
        return ds.CreateLayer(1, geometry);
    }

    /// Whole screen pixels, so pixel art stays hard-edged.
    public static Vector2 Snap(Vector2 at, float scale) =>
        new(MathF.Round(at.X * scale) / scale, MathF.Round(at.Y * scale) / scale);

    /// Classic's floor shadow: a soft ellipse, 30 × 8 DIP, ink at 32 % in
    /// light and black at 60 % in dark, fading out from its centre.
    private static void Floor(CanvasDrawingSession ds, Vector2 centre, float opacity, bool dark)
    {
        var ink = dark ? Color.FromArgb(153, 0, 0, 0) : Color.FromArgb(82, 0x17, 0x19, 0x1F);
        using var brush = new CanvasRadialGradientBrush(ds, ink, Color.FromArgb(0, ink.R, ink.G, ink.B))
        {
            Center = centre,
            RadiusX = 15,
            RadiusY = 4,
            Opacity = opacity,
        };
        ds.FillEllipse(centre, 15, 4, brush);
    }

    /// The frame's silhouette in one colour, keeping its alpha.
    private static ICanvasImage Silhouette(CanvasBitmap frame, Color color)
    {
        // The default alpha mode works on unpremultiplied colour, so clear
        // pixels stay clear.
        _silhouette ??= new ColorMatrixEffect();
        _silhouette.Source = frame;
        _silhouette.ColorMatrix = new Matrix5x4
        {
            M44 = 1,
            M51 = color.R / 255f,
            M52 = color.G / 255f,
            M53 = color.B / 255f,
        };
        return _silhouette;
    }
}
