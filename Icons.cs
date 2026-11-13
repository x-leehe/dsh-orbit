using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace DshStatusWidget;

/// <summary>
/// 图标绘制：几何来自 lucide-static 官方 SVG（见 <see cref="LucideData"/>），
/// 24×24 视口、2 单位描边、圆头圆角，按目标尺寸等比缩放。
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<string, GraphicsPath?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void Draw(
        Graphics g,
        string name,
        RectangleF box,
        Color color,
        float thickness = 2f,
        bool fill = false)
    {
        var path = Get(name);
        if (path is null || box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(box.Width, box.Height) / 24f;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(
            box.X + ((box.Width - (24f * scale)) / 2f),
            box.Y + ((box.Height - (24f * scale)) / 2f));
        g.ScaleTransform(scale, scale);

        if (fill)
        {
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        else
        {
            using var pen = new Pen(color, thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawPath(pen, path);
        }

        g.Restore(state);
    }

    public static bool Exists(string name) => LucideData.Shapes.ContainsKey(name);

    private static GraphicsPath? Get(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var built = Build(name);
            Cache[name] = built;
            return built;
        }
    }

    private static GraphicsPath? Build(string name)
    {
        if (!LucideData.Shapes.TryGetValue(name, out var shapes) || shapes.Length == 0)
        {
            return null;
        }

        var combined = new GraphicsPath();
        foreach (var shape in shapes)
        {
            var separator = shape.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            var kind = shape[..separator];
            var payload = shape[(separator + 1)..];

            using var part = BuildOne(kind, payload);
            if (part is not null && part.PointCount > 0)
            {
                combined.AddPath(part, connect: false);
            }
        }

        return combined.PointCount == 0 ? null : combined;
    }

    private static GraphicsPath? BuildOne(string kind, string payload)
    {
        switch (kind)
        {
            case "P":
                return SvgPath.Parse(payload);

            case "R":
            {
                var parts = SplitNumbers(payload);
                if (parts.Length < 4)
                {
                    return null;
                }

                var x = parts[0];
                var y = parts[1];
                var w = parts[2];
                var h = parts[3];
                var radius = parts.Length > 4 ? parts[4] : 0;
                var path = new GraphicsPath();
                if (radius <= 0)
                {
                    path.AddRectangle(new RectangleF(x, y, w, h));
                }
                else
                {
                    AddRounded(path, new RectangleF(x, y, w, h), radius);
                }

                return path;
            }

            case "C":
            {
                var parts = SplitNumbers(payload);
                if (parts.Length < 3)
                {
                    return null;
                }

                var path = new GraphicsPath();
                path.AddEllipse(parts[0] - parts[2], parts[1] - parts[2], parts[2] * 2, parts[2] * 2);
                return path;
            }

            case "L":
            {
                var parts = SplitNumbers(payload);
                if (parts.Length < 4)
                {
                    return null;
                }

                var path = new GraphicsPath();
                path.AddLine(parts[0], parts[1], parts[2], parts[3]);
                return path;
            }

            case "O":
            case "G":
            {
                var points = SplitPoints(payload);
                var path = new GraphicsPath();
                if (points.Length >= 2)
                {
                    if (kind == "G")
                    {
                        path.AddPolygon(points);
                    }
                    else
                    {
                        path.AddLines(points);
                    }
                }

                return path;
            }

            default:
                return null;
        }
    }

    private static void AddRounded(GraphicsPath path, RectangleF rect, float radius)
    {
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
    }

    private static float[] SplitNumbers(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            result[i] = float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        }

        return result;
    }

    private static PointF[] SplitPoints(string value)
    {
        var numbers = SplitNumbers(value.Replace(',', '|').Replace(' ', '|'));
        var points = new PointF[numbers.Length / 2];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new PointF(numbers[i * 2], numbers[(i * 2) + 1]);
        }

        return points;
    }
}
