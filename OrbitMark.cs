using System.Drawing;
using System.Drawing.Drawing2D;

namespace DshOrbit;

/// <summary>
/// DSH-Orbit 的标志：三段圆弧组成的光圈（aperture / 轨道），中心是 DSH 的鲸鱼标。
/// 纯 Graphics 绘制，所以托盘 16px、标题栏 24px、exe 图标共用同一份几何。
/// </summary>
internal static class OrbitMark
{
    /// <summary>在方框内绘制标志；stroke 为光圈线宽（像素）。</summary>
    public static void Draw(Graphics g, RectangleF box, Color accent, float stroke, bool withWhale = true)
    {
        var size = Math.Min(box.Width, box.Height);
        if (size <= 0)
        {
            return;
        }

        var cx = box.X + (box.Width / 2f);
        var cy = box.Y + (box.Height / 2f);
        var radius = size * 0.34f;

        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(accent, Math.Max(1f, stroke))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // 三段 74° 弧，间隔 120°
        for (var i = 0; i < 3; i++)
        {
            var start = -90 + (i * 120);
            g.DrawArc(pen, cx - radius, cy - radius, radius * 2, radius * 2, start, 74);
        }

        var whaleBox = size * 0.34f;
        if (withWhale && whaleBox >= 7f)
        {
            DshMark.Draw(
                g,
                new RectangleF(cx - (whaleBox / 2f), cy - (whaleBox / 2f), whaleBox, whaleBox),
                accent);
        }
        else
        {
            using var brush = new SolidBrush(accent);
            var dot = Math.Max(2f, size * 0.13f);
            g.FillEllipse(brush, cx - (dot / 2f), cy - (dot / 2f), dot, dot);
        }

        g.Restore(state);
    }

    /// <summary>带圆角深色底板的完整图标（用于 exe / 托盘大图）。</summary>
    public static void DrawTile(Graphics g, RectangleF box, Color accent, Color background)
    {
        var radius = box.Width * 0.22f;
        using (var path = Rounded(box, radius))
        using (var fill = new LinearGradientBrush(box, Lighten(background, 0.10f), background, 90f))
        {
            g.FillPath(fill, path);
        }

        var inset = box.Width * 0.18f;
        Draw(g, RectangleF.Inflate(box, -inset, -inset), accent, Math.Max(1.2f, box.Width * 0.055f));
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        color.A,
        (int)Math.Min(255, color.R + (255 - color.R) * amount),
        (int)Math.Min(255, color.G + (255 - color.G) * amount),
        (int)Math.Min(255, color.B + (255 - color.B) * amount));

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var arc = new RectangleF(rect.X, rect.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
