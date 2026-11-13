using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshStatusWidget;

/// <summary>用代码画一个托盘图标：深色圆角方块 + 状态点。</summary>
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly List<IntPtr> Handles = new();
    private static readonly Dictionary<int, Icon> Cache = new();
    private static readonly object Gate = new();

    public static Icon Create(Color accent)
    {
        var key = accent.ToArgb();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var icon = Build(accent);

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Cache[key] = icon;
        }

        return icon;
    }

    private static Icon Build(Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            OrbitMark.DrawTile(g, new RectangleF(0, 0, 32, 32), accent, Color.FromArgb(20, 25, 34));
        }

        var hIcon = bitmap.GetHicon();
        lock (Gate)
        {
            Handles.Add(hIcon);
        }

        return Icon.FromHandle(hIcon);
    }
    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        var arc = new Rectangle(rect.X, rect.Y, d, d);
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

    public static void ReleaseAll()
    {
        lock (Gate)
        {
            foreach (var handle in Handles)
            {
                _ = DestroyIcon(handle);
            }

            Handles.Clear();
        }
    }
}
