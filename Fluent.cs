using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace DshOrbit;

internal enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>WinUI 3 的版式阶梯（effective px）。</summary>
internal enum TextRole
{
    Caption,
    Body,
    BodyLarge,
    Subtitle,
    Title,
    Display,
}

internal enum VisualState
{
    Rest,
    Hover,
    Pressed,
    Disabled,
}

/// <summary>按钮/底板的语义角色。</summary>
internal enum SurfaceRole
{
    Standard,
    Accent,
    Subtle,
    Danger,
}

/// <summary>
/// WinUI 3（Fluent）设计令牌与绘制原语，融合 Material 3 的状态层、抬升与形状体系。
/// 参考：Windows 几何（4/8px 圆角）、版式阶梯、8/12/16 间距网格、Fluent 色彩角色。
/// 全程序只允许这里出现具体的颜色字面量，界面文件一律通过令牌取色。
/// </summary>
internal static class Fluent
{
    // ---------------------------------------------------------------- 几何

    /// <summary>控件、条状元素（WinUI 标准控件）。</summary>
    public const float RControl = 4f;

    /// <summary>顶层容器、浮出层、卡片（WinUI overlay 圆角）。</summary>
    public const float RCard = 8f;

    /// <summary>Material 3 大磁贴圆角。</summary>
    public const float RTile = 16f;

    public const float RPill = 999f;

    public const int G4 = 4;
    public const int G8 = 8;
    public const int G12 = 12;
    public const int G16 = 16;
    public const int G24 = 24;
    public const int G32 = 32;

    // ---------------------------------------------------------------- 主题状态

    private static readonly Color DefaultAccent = Color.FromArgb(0x00, 0x78, 0xD4);

    private static readonly Dictionary<(int Role, bool Strong, int Scale), Font> FontCache = new();
    private static readonly Dictionary<(int Align, int Trim), StringFormat> FormatCache = new();

    public static AppTheme Mode { get; private set; } = AppTheme.System;

    public static bool IsDark { get; private set; } = true;

    /// <summary>主题或强调色变化后触发，窗口据此重绘。</summary>
    public static event Action? Changed;

    public static Color Mica { get; private set; } = Color.FromArgb(32, 32, 32);
    public static Color Layer { get; private set; } = Color.FromArgb(43, 43, 43);
    public static Color LayerAlt { get; private set; } = Color.FromArgb(50, 50, 50);
    public static Color Flyout { get; private set; } = Color.FromArgb(44, 44, 44);

    public static Color ControlFill { get; private set; } = Color.FromArgb(48, 48, 48);
    public static Color ControlFillHover { get; private set; } = Color.FromArgb(56, 56, 56);
    public static Color ControlFillPressed { get; private set; } = Color.FromArgb(42, 42, 42);
    public static Color ControlFillDisabled { get; private set; } = Color.FromArgb(40, 40, 40);

    public static Color StrokeColor { get; private set; } = Color.FromArgb(18, 255, 255, 255);
    public static Color StrokeStrong { get; private set; } = Color.FromArgb(31, 255, 255, 255);
    public static Color DividerColor { get; private set; } = Color.FromArgb(21, 255, 255, 255);

    public static Color TextPrimary { get; private set; } = Color.FromArgb(255, 255, 255);
    public static Color TextSecondary { get; private set; } = Color.FromArgb(197, 255, 255, 255);
    public static Color TextTertiary { get; private set; } = Color.FromArgb(138, 255, 255, 255);
    public static Color TextDisabled { get; private set; } = Color.FromArgb(93, 255, 255, 255);

    public static Color AccentFill { get; private set; } = Color.FromArgb(0x4C, 0xC2, 0xFF);
    public static Color AccentFillHover { get; private set; } = Color.FromArgb(0x6C, 0xD0, 0xFF);
    public static Color AccentFillPressed { get; private set; } = Color.FromArgb(0x2A, 0xA6, 0xE0);
    public static Color AccentText { get; private set; } = Color.FromArgb(0x6C, 0xD0, 0xFF);
    public static Color OnAccent { get; private set; } = Color.FromArgb(0, 0, 0);

    public static Color Success { get; private set; } = Color.FromArgb(0x6C, 0xCB, 0x5F);
    public static Color Caution { get; private set; } = Color.FromArgb(0xFC, 0xE1, 0x00);
    public static Color Critical { get; private set; } = Color.FromArgb(0xFF, 0x99, 0xA4);

    // macOS 红绿灯三色：这是刻意的「系统级仿制」，不随主题变，也不算通用语义色。
    public static Color TrafficClose { get; } = Color.FromArgb(0xFF, 0x5F, 0x57);
    public static Color TrafficMinimize { get; } = Color.FromArgb(0xFE, 0xBC, 0x2E);
    public static Color TrafficMaximize { get; } = Color.FromArgb(0x28, 0xC8, 0x40);

    /// <summary>悬停/按下状态层（M3 state layer）。</summary>
    public static Color HoverLayer => IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);

    public static Color PressLayer => IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0);

    // ---------------------------------------------------------------- 字体

    public static string BodyFamily { get; } = Pick("Segoe UI Variable Text", "Segoe UI");

    public static string StrongFamily { get; } = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");

    public static string DisplayFamily { get; } = Pick("Segoe UI Variable Display", "Segoe UI");

    /// <summary>注意：Windows 家族名上限 31 字符，"Segoe UI Variable Display Semibold" 会被截断。</summary>
    public static string DisplayStrongFamily { get; } =
        Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");

    public static string SmallFamily { get; } = Pick("Segoe UI Variable Small", "Segoe UI");

    public static string SmallStrongFamily { get; } =
        Pick("Segoe UI Variable Small Semibol", "Segoe UI Semibold", "Segoe UI");

    /// <summary>等宽：只用于端口号这类需要对齐的数字。</summary>
    public static string MonoFamily { get; } = Pick("Cascadia Mono", "Consolas");

    // ---------------------------------------------------------------- 初始化

    public static void Init(AppTheme mode)
    {
        Mode = mode;
        Apply();
    }

    public static void SetMode(AppTheme mode)
    {
        Mode = mode;
        Apply();
    }

    /// <summary>定时器里调用：系统主题/强调色变了才重绘。</summary>
    public static bool Refresh() => Apply(force: false);

    private static bool Apply(bool force = true)
    {
        var dark = Mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => ReadSystemDark(),
        };

        var accent = ReadAccentColor() ?? DefaultAccent;

        var before = (IsDark, AccentFill);
        IsDark = dark;

        if (dark)
        {
            Mica = Color.FromArgb(32, 32, 32);
            Layer = Color.FromArgb(43, 43, 43);
            LayerAlt = Color.FromArgb(50, 50, 50);
            Flyout = Color.FromArgb(44, 44, 44);

            ControlFill = Color.FromArgb(48, 48, 48);
            ControlFillHover = Color.FromArgb(59, 59, 59);
            ControlFillPressed = Color.FromArgb(41, 41, 41);
            ControlFillDisabled = Color.FromArgb(38, 38, 38);

            StrokeColor = Color.FromArgb(18, 255, 255, 255);
            StrokeStrong = Color.FromArgb(34, 255, 255, 255);
            DividerColor = Color.FromArgb(21, 255, 255, 255);

            TextPrimary = Color.FromArgb(255, 255, 255);
            TextSecondary = Color.FromArgb(197, 255, 255, 255);
            TextTertiary = Color.FromArgb(138, 255, 255, 255);
            TextDisabled = Color.FromArgb(93, 255, 255, 255);

            // WinUI 深色下强调色取浅阶，按钮文字用黑色。
            AccentFill = Shade(accent, 0.26);
            AccentFillHover = Shade(accent, 0.36);
            AccentFillPressed = Shade(accent, 0.12);
            AccentText = Shade(accent, 0.34);
            OnAccent = Color.FromArgb(8, 8, 8);

            Success = Color.FromArgb(0x6C, 0xCB, 0x5F);
            Caution = Color.FromArgb(0xFC, 0xE1, 0x00);
            Critical = Color.FromArgb(0xFF, 0x99, 0xA4);
        }
        else
        {
            Mica = Color.FromArgb(0xF3, 0xF3, 0xF3);
            Layer = Color.FromArgb(0xFB, 0xFB, 0xFB);
            LayerAlt = Color.FromArgb(0xF2, 0xF4, 0xF7);
            Flyout = Color.FromArgb(0xF9, 0xF9, 0xF9);

            ControlFill = Color.FromArgb(0xFD, 0xFD, 0xFD);
            ControlFillHover = Color.FromArgb(0xF6, 0xF6, 0xF6);
            ControlFillPressed = Color.FromArgb(0xEF, 0xEF, 0xEF);
            ControlFillDisabled = Color.FromArgb(0xF5, 0xF5, 0xF5);

            StrokeColor = Color.FromArgb(15, 0, 0, 0);
            StrokeStrong = Color.FromArgb(41, 0, 0, 0);
            DividerColor = Color.FromArgb(20, 0, 0, 0);

            TextPrimary = Color.FromArgb(228, 0, 0, 0);
            TextSecondary = Color.FromArgb(158, 0, 0, 0);
            TextTertiary = Color.FromArgb(114, 0, 0, 0);
            TextDisabled = Color.FromArgb(92, 0, 0, 0);

            AccentFill = Shade(accent, -0.06);
            AccentFillHover = Shade(accent, 0.02);
            AccentFillPressed = Shade(accent, -0.14);
            AccentText = Shade(accent, -0.12);
            OnAccent = Color.White;

            Success = Color.FromArgb(0x0F, 0x7B, 0x0F);
            Caution = Color.FromArgb(0x9D, 0x5D, 0x00);
            Critical = Color.FromArgb(0xC4, 0x2B, 0x1C);
        }

        var changed = force || before != (IsDark, AccentFill);
        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return true;
        }
    }

    private static Color? ReadAccentColor()
    {
        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr && abgr != 0)
            {
                // AccentColor 是 0xAABBGGRR
                var r = abgr & 0xFF;
                var g = (abgr >> 8) & 0xFF;
                var b = (abgr >> 16) & 0xFF;
                return Color.FromArgb(r, g, b);
            }

            if (dwm?.GetValue("ColorizationColor") is int argb && argb != 0)
            {
                // ColorizationColor 是 0xAARRGGBB
                var r = (argb >> 16) & 0xFF;
                var g = (argb >> 8) & 0xFF;
                var b = argb & 0xFF;
                return Color.FromArgb(r, g, b);
            }
        }
        catch
        {
            // 读不到就用默认强调色
        }

        return null;
    }

    /// <summary>按亮度推强调色的浅/深阶（相当于 WinUI 的 AccentLight/AccentDark）。</summary>
    private static Color Shade(Color color, double amount)
    {
        var (h, s, l) = ToHsl(color);
        l = Math.Clamp(l + amount, 0.06, 0.94);
        if (amount > 0)
        {
            s = Math.Max(0.12, s * 0.92);
        }

        return FromHsl(h, s, l);
    }

    private static (double H, double S, double L) ToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-6)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (Math.Abs(max - r) < 1e-6)
        {
            h = ((g - b) / d) + (g < b ? 6 : 0);
        }
        else if (Math.Abs(max - g) < 1e-6)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            var v = (int)Math.Round(l * 255);
            return Color.FromArgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;

        static double Hue(double t, double p, double q)
        {
            if (t < 0)
            {
                t += 1;
            }

            if (t > 1)
            {
                t -= 1;
            }

            if (t < 1.0 / 6)
            {
                return p + ((q - p) * 6 * t);
            }

            if (t < 0.5)
            {
                return q;
            }

            if (t < 2.0 / 3)
            {
                return p + ((q - p) * ((2.0 / 3) - t) * 6);
            }

            return p;
        }

        return Color.FromArgb(
            (int)Math.Round(Hue(h + (1.0 / 3), p, q) * 255),
            (int)Math.Round(Hue(h, p, q) * 255),
            (int)Math.Round(Hue(h - (1.0 / 3), p, q) * 255));
    }

    private static string Pick(params string[] candidates)
    {
        foreach (var name in candidates)
        {
            if (FamilyExists(name))
            {
                return name;
            }
        }

        return candidates[^1];
    }

    private static bool FamilyExists(string name)
    {
        try
        {
            using var family = new FontFamily(name);
            return !string.IsNullOrEmpty(family.Name);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 版式

    public static Font Font(TextRole role, bool strong = false, float scale = 1f)
    {
        var key = ((int)role, strong, (int)Math.Round(scale * 20));
        if (FontCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var size = role switch
        {
            TextRole.Caption => 12f,
            TextRole.Body => 14f,
            TextRole.BodyLarge => 18f,
            TextRole.Subtitle => 20f,
            TextRole.Title => 28f,
            _ => 40f,
        };

        var family = role switch
        {
            TextRole.Caption => strong ? SmallStrongFamily : SmallFamily,
            TextRole.Title or TextRole.Display => strong ? DisplayStrongFamily : DisplayFamily,
            _ => strong ? StrongFamily : BodyFamily,
        };

        var font = new Font(family, size * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        FontCache[key] = font;
        return font;
    }

    public static float LineHeight(TextRole role) => role switch
    {
        TextRole.Caption => 16f,
        TextRole.Body => 20f,
        TextRole.BodyLarge => 24f,
        TextRole.Subtitle => 28f,
        TextRole.Title => 36f,
        _ => 52f,
    };

    private static StringFormat Format(StringAlignment align, StringTrimming trim)
    {
        var key = ((int)align, (int)trim);
        if (FormatCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = align;
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags = StringFormatFlags.NoWrap;
        if (trim != StringTrimming.None)
        {
            format.Trimming = trim;
        }

        FormatCache[key] = format;
        return format;
    }

    public static void Text(
        Graphics g,
        string text,
        TextRole role,
        bool strong,
        Color color,
        RectangleF rect,
        StringAlignment align = StringAlignment.Near,
        StringTrimming trim = StringTrimming.None,
        float scale = 1f)
        => Text(g, text, Font(role, strong, scale), color, rect, align, trim);

    public static void Text(
        Graphics g,
        string text,
        Font font,
        Color color,
        RectangleF rect,
        StringAlignment align = StringAlignment.Near,
        StringTrimming trim = StringTrimming.None)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, Format(align, trim));
    }

    public static float Measure(Graphics g, string text, Font font) =>
        string.IsNullOrEmpty(text)
            ? 0f
            : g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    public static float Measure(Graphics g, string text, TextRole role, bool strong = false, float scale = 1f) =>
        Measure(g, text, Font(role, strong, scale));

    // ---------------------------------------------------------------- 图形基础

    public static void AntiAlias(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }

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

    /// <summary>只圆上面两个角（标签、卡片头）。</summary>
    public static GraphicsPath RoundedTop(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var arc = new RectangleF(rect.X, rect.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, GraphicsPath path, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void Fill(Graphics g, RectangleF rect, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, rect);
    }

    public static void Stroke(Graphics g, GraphicsPath path, Color color, float width = 1f)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
    }

    /// <summary>M3 状态层：在已有底色上叠一层透明遮罩。</summary>
    public static Color OverState(Color baseColor, VisualState state)
    {
        var layer = state switch
        {
            VisualState.Hover => HoverLayer,
            VisualState.Pressed => PressLayer,
            _ => Color.Empty,
        };

        return layer.IsEmpty ? baseColor : Mix(baseColor, layer, Alpha(layer) / 255f);
    }

    public static int Alpha(Color color) => color.A;

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + ((b.A - a.A) * t)),
            (int)Math.Round(a.R + ((b.R - a.R) * t)),
            (int)Math.Round(a.G + ((b.G - a.G) * t)),
            (int)Math.Round(a.B + ((b.B - a.B) * t)));
    }

    public static Color WithAlpha(Color color, float alpha) =>
        Color.FromArgb(Math.Clamp((int)Math.Round(alpha * 255), 0, 255), color.R, color.G, color.B);

    public static int Lerp(int from, int to, float t) => (int)Math.Round(from + ((to - from) * t));

    public static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    /// <summary>WinUI 的标准缓出。</summary>
    public static float EaseOut(float t)
    {
        t = Clamp01(t);
        var inv = 1 - t;
        return 1 - (inv * inv * inv);
    }

    /// <summary>M3 emphasized（近似 cubic-bezier(0.2, 0, 0, 1)）。</summary>
    public static float EaseEmphasized(float t)
    {
        t = Clamp01(t);
        return (float)(1 - Math.Pow(1 - t, 4.2));
    }

    // ---------------------------------------------------------------- 海拔

    /// <summary>M3 抬升阴影，level 0–3。0 表示不画。</summary>
    public static void Shadow(Graphics g, RectangleF rect, float radius, int level)
    {
        var (rings, alpha, spread, offsetY) = level switch
        {
            1 => (5, 4, 1.2f, 1),
            2 => (8, 3, 1.2f, 2),
            3 => (16, 2, 1.6f, 3),
            _ => (0, 0, 0f, 0),
        };

        if (rings == 0)
        {
            return;
        }

        for (var i = rings; i >= 1; i--)
        {
            var grow = i * spread;
            var r = new RectangleF(
                rect.X - grow,
                rect.Y - grow + offsetY,
                rect.Width + (2 * grow),
                rect.Height + (2 * grow));
            using var path = Rounded(r, radius + grow);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    // ---------------------------------------------------------------- 卡片

    /// <summary>WinUI 卡片：层色 + 1px 描边（上亮下暗）+ 可选海拔。</summary>
    public static void Card(Graphics g, RectangleF rect, float radius = RCard, int elevation = 0, Color? fill = null)
    {
        if (elevation > 0)
        {
            Shadow(g, rect, radius, elevation);
        }

        using var path = Rounded(rect, radius);
        Fill(g, path, fill ?? Layer);

        // 描边用线性渐变模拟 WinUI 的"上边略亮"。
        using (var edge = Rounded(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius))
        {
            using var gradient = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, rect.Width, Math.Max(rect.Height, 2)),
                IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0),
                IsDark ? Color.FromArgb(12, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0),
                90f);
            using var pen = new Pen(gradient, 1f);
            g.DrawPath(pen, edge);
        }
    }

    public static void Divider(Graphics g, RectangleF rect)
    {
        using var brush = new SolidBrush(DividerColor);
        g.FillRectangle(brush, rect);
    }

    // ---------------------------------------------------------------- 控件

    public static Color FillOf(SurfaceRole role, VisualState state)
    {
        if (state == VisualState.Disabled)
        {
            return role == SurfaceRole.Subtle ? Color.Transparent : ControlFillDisabled;
        }

        return role switch
        {
            SurfaceRole.Accent => state switch
            {
                VisualState.Hover => AccentFillHover,
                VisualState.Pressed => AccentFillPressed,
                _ => AccentFill,
            },
            SurfaceRole.Subtle => state switch
            {
                VisualState.Hover => OverState(Layer, VisualState.Hover),
                VisualState.Pressed => OverState(Layer, VisualState.Pressed),
                _ => Color.Transparent,
            },
            SurfaceRole.Danger => state switch
            {
                VisualState.Hover => Mix(ControlFill, Critical, 0.14f),
                VisualState.Pressed => Mix(ControlFill, Critical, 0.22f),
                _ => ControlFill,
            },
            _ => state switch
            {
                VisualState.Hover => ControlFillHover,
                VisualState.Pressed => ControlFillPressed,
                _ => ControlFill,
            },
        };
    }

    public static Color TextOf(SurfaceRole role, VisualState state)
    {
        if (state == VisualState.Disabled)
        {
            return TextDisabled;
        }

        return role switch
        {
            SurfaceRole.Accent => OnAccent,
            SurfaceRole.Danger => Critical,
            _ => TextPrimary,
        };
    }

    /// <summary>WinUI 按钮：32px 高、4px 圆角、1px 描边、可选前置图标。</summary>
    public static void Button(
        Graphics g,
        RectangleF rect,
        string label,
        SurfaceRole role = SurfaceRole.Standard,
        VisualState state = VisualState.Rest,
        string? icon = null,
        float scale = 1f,
        bool center = true)
    {
        var radius = RControl;
        var fill = FillOf(role, state);
        using (var path = Rounded(rect, radius))
        {
            if (fill != Color.Transparent)
            {
                Fill(g, path, fill);
            }

            if (role is SurfaceRole.Standard or SurfaceRole.Danger)
            {
                Stroke(g, path, IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0), 1f);
            }
            else if (role == SurfaceRole.Accent && state != VisualState.Disabled)
            {
                // WinUI 强调按钮顶部的一条高光
                using var highlight = new Pen(Color.FromArgb(IsDark ? 0 : 40, 255, 255, 255), 1f);
                g.DrawPath(highlight, path);
            }
        }

        var color = TextOf(role, state);
        var font = Font(TextRole.Body, false, scale);
        var textWidth = Measure(g, label, font);
        var iconSize = 16f * Math.Max(1f, scale * 0.6f);

        if (icon is not null)
        {
            var hasLabel = !string.IsNullOrEmpty(label);
            var total = iconSize + (hasLabel ? 8f + textWidth : 0);
            var startX = center ? rect.X + ((rect.Width - total) / 2f) : rect.X + 12f;
            var iconRect = new RectangleF(startX, rect.Y + ((rect.Height - iconSize) / 2f), iconSize, iconSize);
            Icons.Draw(g, icon, iconRect, color, 2f);
            if (hasLabel)
            {
                Text(g, label, font, color,
                    new RectangleF(iconRect.Right + 8f, rect.Y, rect.Width, rect.Height));
            }

            return;
        }

        Text(g, label, font, color, rect,
            center ? StringAlignment.Center : StringAlignment.Near,
            StringTrimming.EllipsisCharacter);
    }

    /// <summary>32×32 图标按钮，悬停时出现 4px 圆角背板。</summary>
    public static void IconButton(
        Graphics g,
        RectangleF rect,
        string icon,
        VisualState state = VisualState.Rest,
        float iconSize = 16f,
        Color? color = null,
        Color? hoverTint = null)
    {
        if (state is VisualState.Hover or VisualState.Pressed)
        {
            using var path = Rounded(rect, RControl);
            Fill(g, path, hoverTint ?? HoverLayer);
            if (state == VisualState.Pressed)
            {
                Fill(g, path, PressLayer);
            }
        }

        var fg = color ?? (state == VisualState.Disabled ? TextDisabled : TextPrimary);
        var box = new RectangleF(
            rect.X + ((rect.Width - iconSize) / 2f),
            rect.Y + ((rect.Height - iconSize) / 2f),
            iconSize,
            iconSize);
        Icons.Draw(g, icon, box, fg, 2f);
    }

    /// <summary>M3 分段控件：WinUI 的 4px 圆角 + M3 的选中态着色。</summary>
    public static void Segmented(
        Graphics g,
        RectangleF rect,
        IReadOnlyList<string> labels,
        int selected,
        int hoverIndex = -1,
        float scale = 1f)
    {
        using var track = Rounded(rect, RControl);
        Fill(g, track, IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0));
        Stroke(g, track, StrokeColor, 1f);

        if (labels.Count == 0)
        {
            return;
        }

        var inset = 2f * scale;
        var segment = (rect.Width - (2 * inset)) / labels.Count;
        for (var i = 0; i < labels.Count; i++)
        {
            var cell = new RectangleF(rect.X + inset + (i * segment), rect.Y + inset, segment, rect.Height - (2 * inset));
            var active = i == selected;

            if (active)
            {
                using var path = Rounded(cell, RControl - 1);
                Fill(g, path, Mix(Layer, AccentFill, 0.24f));
                Stroke(g, path, Mix(StrokeStrong, AccentFill, 0.7f), 1f);
            }
            else if (i == hoverIndex)
            {
                using var path = Rounded(cell, RControl - 1);
                Fill(g, path, HoverLayer);
            }

            Text(g, labels[i], Font(TextRole.Caption, active, scale),
                active ? TextPrimary : TextSecondary,
                cell, StringAlignment.Center, StringTrimming.EllipsisCharacter);
        }
    }

    /// <summary>MenuFlyout 里的一行：16px 图标 + 14px 文字 + 右侧 12px 状态。</summary>
    public static void MenuRow(
        Graphics g,
        RectangleF rect,
        string? icon,
        string label,
        string? trailing,
        VisualState state = VisualState.Rest,
        Color? foreground = null,
        bool trailingAccent = false,
        float scale = 1f)
    {
        var backplate = new RectangleF(rect.X + 4, rect.Y + 1, rect.Width - 8, rect.Height - 2);
        if (state is VisualState.Hover or VisualState.Pressed)
        {
            using var path = Rounded(backplate, RControl);
            Fill(g, path, state == VisualState.Pressed ? PressLayer : HoverLayer);
        }

        var fg = foreground ?? (state == VisualState.Disabled ? TextDisabled : TextPrimary);
        var iconSize = 16f * scale;
        var padX = 12f * scale;
        var x = rect.X + padX;
        if (icon is not null)
        {
            Icons.Draw(g, icon,
                new RectangleF(x, rect.Y + ((rect.Height - iconSize) / 2f), iconSize, iconSize),
                fg,
                2f);
            x += iconSize + padX;
        }

        // 右侧内边距统一 12：状态值右对齐到该处，标题的可用宽度也截止到它左侧 8px。
        var trailingPad = 12f * scale;
        var labelRight = rect.Right - trailingPad;
        if (!string.IsNullOrEmpty(trailing))
        {
            var trailingWidth = Measure(g, trailing!, Font(TextRole.Caption, false, scale)) + 4f;
            var trailingRect = new RectangleF(labelRight - trailingWidth, rect.Y, trailingWidth, rect.Height);
            Text(g, trailing!, Font(TextRole.Caption, false, scale),
                trailingAccent ? AccentText : TextTertiary, trailingRect, StringAlignment.Far);
            labelRight = trailingRect.X - (8f * scale);
        }

        Text(g, label, Font(TextRole.Body, false, scale), fg,
            new RectangleF(x, rect.Y, Math.Max(0f, labelRight - x), rect.Height),
            StringAlignment.Near, StringTrimming.EllipsisCharacter);
    }

    /// <summary>分组标题（12px 三级文字）。</summary>
    public static void SectionLabel(Graphics g, RectangleF rect, string text, float scale = 1f) =>
        Text(g, text, Font(TextRole.Caption, true, scale), TextTertiary, rect);

    /// <summary>WinUI 状态药丸：12px 文字 + 前导圆点。</summary>
    public static void Pill(
        Graphics g,
        RectangleF rect,
        string text,
        Color foreground,
        Color background,
        bool dot = false,
        float scale = 1f)
    {
        using var path = Rounded(rect, RPill);
        Fill(g, path, background);

        var x = rect.X + 10f * scale;
        if (dot)
        {
            var d = 6f * scale;
            using var brush = new SolidBrush(foreground);
            g.FillEllipse(brush, x, rect.Y + ((rect.Height - d) / 2f), d, d);
            x += d + 6f * scale;
        }

        Text(g, text, Font(TextRole.Caption, true, scale), foreground,
            new RectangleF(x, rect.Y, rect.Right - x - (10f * scale), rect.Height));
    }

    /// <summary>WinUI TextBox：32px、4px 圆角、聚焦时强调色下划线。</summary>
    public static void TextBox(
        Graphics g,
        RectangleF rect,
        string? text,
        string? placeholder,
        bool focused,
        bool hovered,
        float scale = 1f,
        float radius = RControl,
        float textInset = 11f)
    {
        using var path = Rounded(rect, radius);
        Fill(g, path, hovered ? ControlFillHover : ControlFill);
        Stroke(g, path, focused ? StrokeStrong : StrokeColor, 1f);

        if (focused)
        {
            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using var brush = new SolidBrush(AccentFill);
            g.FillRectangle(brush, rect.X, rect.Bottom - (2 * scale), rect.Width, 2 * scale);
            g.Restore(state);
        }

        var shown = string.IsNullOrEmpty(text) ? null : text;
        var color = shown is null ? TextTertiary : TextPrimary;
        var label = shown ?? placeholder ?? string.Empty;
        Text(g, label, Font(TextRole.Body, false, scale), color,
            new RectangleF(rect.X + textInset, rect.Y, rect.Width - (2 * textInset), rect.Height),
            StringAlignment.Near, StringTrimming.EllipsisCharacter);
    }

    /// <summary>M3 大磁贴：16px 圆角、悬停抬升一级、可叠加涟漪。</summary>
    public static void Tile(
        Graphics g,
        RectangleF rect,
        VisualState state = VisualState.Rest,
        float radius = RTile,
        bool accentEdge = false,
        bool outlined = true)
    {
        var fill = state switch
        {
            VisualState.Hover => Mix(Layer, AccentFill, IsDark ? 0.07f : 0.05f),
            VisualState.Pressed => Mix(Layer, AccentFill, IsDark ? 0.12f : 0.09f),
            _ => Layer,
        };

        if (state == VisualState.Hover)
        {
            Shadow(g, rect, radius, 1);
        }

        using var path = Rounded(rect, radius);
        Fill(g, path, fill);

        if (outlined)
        {
            var edge = accentEdge
                ? Mix(StrokeStrong, AccentFill, 0.6f)
                : IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
            Stroke(g, path, edge, 1f);
        }
    }

    /// <summary>顶部一条 1px 分隔线（标签条、工具条底部）。</summary>
    public static void HairLine(Graphics g, float x, float y, float width) =>
        Divider(g, new RectangleF(x, y, width, 1));
}

/// <summary>M3 涟漪：按下时从落点扩散的圆形状态层，200ms 缓出。</summary>
internal sealed class Ripple
{
    private const int DurationMs = 220;
    private long _start;
    private PointF _origin;

    public bool Active { get; private set; }

    public PointF Origin => _origin;

    public void Begin(PointF origin)
    {
        _origin = origin;
        _start = Environment.TickCount64;
        Active = true;
    }

    public void Stop() => Active = false;

    public float Progress
    {
        get
        {
            if (!Active)
            {
                return 1f;
            }

            var t = (Environment.TickCount64 - _start) / (float)DurationMs;
            if (t >= 1f)
            {
                Active = false;
                return 1f;
            }

            return Math.Clamp(t, 0f, 1f);
        }
    }

    public void Draw(Graphics g, RectangleF bounds, float radius, Color tint)
    {
        if (!Active)
        {
            return;
        }

        var progress = Fluent.EaseOut(Progress);
        var reach = (float)Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height));
        var size = reach * progress;
        var alpha = (int)Math.Round(28 * (1f - progress));

        using var clip = Fluent.Rounded(bounds, radius);
        var state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 60), tint));
        g.FillEllipse(brush, _origin.X - size, _origin.Y - size, size * 2, size * 2);
        g.Restore(state);
    }
}
