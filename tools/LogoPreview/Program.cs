using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 生成 logo 候选图：每个候选画 大/中/小 三档，检查缩到 16px 是否还认得出。
var outPath = args.Length > 0 ? args[0] : "docs/logo-candidates.png";

const int cell = 200;
const int count = 6;
using var sheet = new Bitmap(cell * count, cell + 60);
using var g = Graphics.FromImage(sheet);
g.Clear(Color.FromArgb(13, 16, 22));
g.SmoothingMode = SmoothingMode.AntiAlias;
g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

var names = new[] { "Bolt-D", "Orbit", "Spark", "Chevrons", "Aperture", "Wave" };
var accent = Color.FromArgb(122, 170, 255);
var accent2 = Color.FromArgb(85, 140, 255);
using var labelFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
using var smallFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);

for (var i = 0; i < count; i++)
{
    var x = i * cell;
    // 大图标：128 圆角底板
    using (var bg = Rounded(new RectangleF(x + 36, 24, 128, 128), 30))
    {
        using var fill = new LinearGradientBrush(new Rectangle(x + 36, 24, 128, 128),
            Color.FromArgb(28, 36, 50), Color.FromArgb(17, 22, 31), 90f);
        g.FillPath(fill, bg);
        using var border = new Pen(Color.FromArgb(46, 255, 255, 255));
        g.DrawPath(border, bg);
    }

    DrawMark(g, i, new RectangleF(x + 36 + 24, 24 + 24, 80, 80), accent, 7f);

    // 32 / 16 小尺寸
    DrawMark(g, i, new RectangleF(x + 60, 168, 32, 32), accent, 3.2f);
    DrawMark(g, i, new RectangleF(x + 110, 176, 16, 16), accent, 2f);

    g.DrawString(names[i], labelFont, new SolidBrush(Color.FromArgb(226, 232, 242)), x + 36, 206);
    g.DrawString("32px", smallFont, new SolidBrush(Color.FromArgb(120, 132, 150)), x + 58, 186);
    g.DrawString("16px", smallFont, new SolidBrush(Color.FromArgb(120, 132, 150)), x + 104, 192);
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
sheet.Save(outPath, ImageFormat.Png);
Console.WriteLine($"saved {outPath}");

// ------------------------------------------------------------------ 六个候选

static void DrawMark(Graphics g, int index, RectangleF box, Color accent, float stroke)
{
    var w = box.Width;
    var cx = box.X + (w / 2f);
    var cy = box.Y + (w / 2f);
    using var pen = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    using var brush = new SolidBrush(accent);

    switch (index)
    {
        case 0: // Bolt-D：一条竖线 + 弧，缺口是闪电
        {
            var left = box.X + (w * 0.26f);
            var top = box.Y + (w * 0.14f);
            var bottom = box.Y + (w * 0.86f);
            g.DrawLine(pen, left, top, left, bottom);
            using var arc = new GraphicsPath();
            arc.AddArc(new RectangleF(left, top, w * 0.58f, w * 0.72f), -90, 180);
            g.DrawPath(pen, arc);
            // 闪电缺口
            using var bolt = new GraphicsPath();
            bolt.AddLines(new[]
            {
                new PointF(cx + (w * 0.06f), cy - (w * 0.20f)),
                new PointF(cx - (w * 0.10f), cy + (w * 0.02f)),
                new PointF(cx + (w * 0.01f), cy + (w * 0.02f)),
                new PointF(cx - (w * 0.04f), cy + (w * 0.20f)),
            });
            var clear = new Pen(Color.FromArgb(17, 22, 31), stroke * 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawPath(clear, bolt);
            g.DrawPath(pen, bolt);
            break;
        }

        case 1: // Orbit：中心点 + 两段轨道弧
        {
            g.FillEllipse(brush, cx - (w * 0.10f), cy - (w * 0.10f), w * 0.20f, w * 0.20f);
            var orbit = new RectangleF(box.X + (w * 0.10f), box.Y + (w * 0.22f), w * 0.80f, w * 0.56f);
            g.DrawArc(pen, orbit, -35, 120);
            g.DrawArc(pen, orbit, 145, 120);
            break;
        }

        case 2: // Spark：四角星 + 小圆点
        {
            var r = w * 0.34f;
            using var star = new GraphicsPath();
            star.AddBezier(
                new PointF(cx, cy - r), new PointF(cx + (r * 0.18f), cy - (r * 0.18f)),
                new PointF(cx + (r * 0.18f), cy - (r * 0.18f)), new PointF(cx + r, cy));
            star.AddBezier(
                new PointF(cx + r, cy), new PointF(cx + (r * 0.18f), cy + (r * 0.18f)),
                new PointF(cx + (r * 0.18f), cy + (r * 0.18f)), new PointF(cx, cy + r));
            star.AddBezier(
                new PointF(cx, cy + r), new PointF(cx - (r * 0.18f), cy + (r * 0.18f)),
                new PointF(cx - (r * 0.18f), cy + (r * 0.18f)), new PointF(cx - r, cy));
            star.AddBezier(
                new PointF(cx - r, cy), new PointF(cx - (r * 0.18f), cy - (r * 0.18f)),
                new PointF(cx - (r * 0.18f), cy - (r * 0.18f)), new PointF(cx, cy - r));
            star.CloseFigure();
            g.FillPath(brush, star);
            g.FillEllipse(new SolidBrush(Color.FromArgb(200, accent)), cx + (w * 0.26f), cy - (w * 0.30f), w * 0.10f, w * 0.10f);
            break;
        }

        case 3: // Chevrons：三道向右的折线
        {
            for (var k = 0; k < 3; k++)
            {
                var ox = box.X + (w * 0.20f) + (k * w * 0.20f);
                g.DrawLines(pen, new[]
                {
                    new PointF(ox, box.Y + (w * 0.22f)),
                    new PointF(ox + (w * 0.20f), cy),
                    new PointF(ox, box.Y + (w * 0.78f)),
                });
            }
            break;
        }

        case 4: // Aperture：三段弧绕中心旋转
        {
            var rr = w * 0.32f;
            for (var k = 0; k < 3; k++)
            {
                var start = -90 + (k * 120);
                g.DrawArc(pen, cx - rr, cy - rr, rr * 2, rr * 2, start, 74);
            }
            g.FillEllipse(brush, cx - (w * 0.07f), cy - (w * 0.07f), w * 0.14f, w * 0.14f);
            break;
        }

        default: // Wave：一条正弦波 + 端点圆
        {
            using var wave = new GraphicsPath();
            wave.AddBezier(
                new PointF(box.X + (w * 0.12f), cy + (w * 0.14f)),
                new PointF(box.X + (w * 0.30f), cy - (w * 0.34f)),
                new PointF(box.X + (w * 0.50f), cy + (w * 0.34f)),
                new PointF(box.X + (w * 0.68f), cy - (w * 0.06f)));
            wave.AddBezier(
                new PointF(box.X + (w * 0.68f), cy - (w * 0.06f)),
                new PointF(box.X + (w * 0.78f), cy - (w * 0.24f)),
                new PointF(box.X + (w * 0.86f), cy - (w * 0.16f)),
                new PointF(box.X + (w * 0.90f), cy - (w * 0.06f)));
            g.DrawPath(pen, wave);
            break;
        }
    }
}

static GraphicsPath Rounded(RectangleF rect, float radius)
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
