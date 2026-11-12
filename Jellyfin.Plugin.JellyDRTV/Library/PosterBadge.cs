using SkiaSharp;

namespace Jellyfin.Plugin.JellyDRTV.Library;

/// <summary>
/// Draws small badges onto posters: "DRTV" top-left on everything, "DRM" top-right on titles that can't be played.
/// Letters are drawn as simple strokes, so no fonts are needed on the server.
/// </summary>
public static class PosterBadge
{
    /// <summary>Bump when the look changes, so existing posters are redrawn.</summary>
    public const int Version = 2;

    private static readonly Dictionary<char, (float Width, string Path)> Glyphs = new()
    {
        ['D'] = (0.62f, "M0 0 L0 1 M0 0 L0.28 0 C0.78 0 0.78 1 0.28 1 L0 1"),
        ['R'] = (0.6f, "M0 1 L0 0 L0.32 0 C0.7 0 0.7 0.5 0.32 0.5 L0 0.5 M0.3 0.5 L0.6 1"),
        ['T'] = (0.62f, "M0 0 L0.62 0 M0.31 0 L0.31 1"),
        ['V'] = (0.64f, "M0 0 L0.32 1 L0.64 0"),
        ['M'] = (0.74f, "M0 1 L0 0 L0.37 0.62 L0.74 0 L0.74 1"),
    };

    /// <summary>Returns the poster with badges drawn on it, as JPEG. Returns the original bytes if decoding fails.</summary>
    public static byte[] Apply(byte[] image, bool drtv, bool drm)
    {
        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null || (!drtv && !drm))
        {
            return image;
        }

        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0);

        var h = Math.Max(10f, bitmap.Width * 0.045f);
        var pad = bitmap.Width * 0.035f;
        if (drtv)
        {
            DrawPill(canvas, "DRTV", h, pad, pad, new SKColor(0, 0, 0, 200), alignRight: false, bitmap.Width);
        }

        if (drm)
        {
            DrawPill(canvas, "DRM", h, pad, pad, new SKColor(198, 40, 40, 235), alignRight: true, bitmap.Width);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static void DrawPill(SKCanvas canvas, string text, float h, float pad, float top, SKColor background, bool alignRight, int imageWidth)
    {
        var spacing = h * 0.32f;
        var textWidth = text.Sum(c => Glyphs[c].Width * h) + (spacing * (text.Length - 1));
        var boxW = textWidth + (h * 1.3f);
        var boxH = h * 2.1f;
        var left = alignRight ? imageWidth - pad - boxW : pad;

        using var bg = new SKPaint { Color = background, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(left, top, left + boxW, top + boxH), boxH * 0.22f, boxH * 0.22f, bg);

        using var pen = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = h * 0.17f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        var x = left + (h * 0.65f);
        var y = top + ((boxH - h) / 2);
        foreach (var c in text)
        {
            var (width, svg) = Glyphs[c];
            using var path = SKPath.ParseSvgPathData(svg);
            path.Transform(SKMatrix.CreateScale(h, h).PostConcat(SKMatrix.CreateTranslation(x, y)));
            canvas.DrawPath(path, pen);
            x += (width * h) + spacing;
        }
    }
}
