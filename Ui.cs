using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DshOrbit;

/// <summary>两个窗口共用的绘制小工具。</summary>
internal static class Ui
{
    public static readonly StringFormat Left = Create(StringAlignment.Near, null);
    public static readonly StringFormat Right = Create(StringAlignment.Far, null);
    public static readonly StringFormat Center = Create(StringAlignment.Center, null);
    public static readonly StringFormat LeftEllipsis = Create(StringAlignment.Near, StringTrimming.EllipsisCharacter);

    private static StringFormat Create(StringAlignment alignment, StringTrimming? trimming)
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags = StringFormatFlags.NoWrap;
        if (trimming is { } value)
        {
            format.Trimming = value;
        }

        return format;
    }

    public static void Text(
        Graphics g,
        string text,
        Font font,
        Color color,
        RectangleF rect,
        StringFormat? format = null)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, format ?? Left);
    }

    public static float Measure(Graphics g, string text, Font font) =>
        g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    public static GraphicsPath Rounded(RectangleF rect, float radius)
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

    public static GraphicsPath Rounded(Rectangle rect, float radius) =>
        Rounded(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), radius);

    /// <summary>柔和投影：多层低透明度圆角矩形堆出渐变。</summary>
    public static void Shadow(Graphics g, RectangleF card, float radius, int blur, int alpha)
    {
        for (var i = blur; i >= 1; i--)
        {
            var rect = new RectangleF(card.X - i, card.Y - i, card.Width + (2 * i), card.Height + (2 * i));
            using var path = Rounded(rect, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    /// <summary>挑一个本机真实存在的字体族（用构造失败来判断，避免枚举全部字体）。</summary>
    public static string PickFamily(string preferred, string fallback)
    {
        try
        {
            using var family = new FontFamily(preferred);
            return preferred;
        }
        catch
        {
            return fallback;
        }
    }
}
