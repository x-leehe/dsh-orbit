using DshStatusWidget;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 生成 DSH-Orbit 的图标：app.ico（多尺寸）+ 预览图。
var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var accent = Color.FromArgb(122, 170, 255);
var background = Color.FromArgb(16, 20, 28);

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = new List<byte[]>();

foreach (var size in sizes)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        OrbitMark.DrawTile(g, new RectangleF(0, 0, size, size), accent, background);
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    frames.Add(ms.ToArray());
    Console.WriteLine($"  {size}px -> {ms.Length} bytes");
}

// 预览：透明版（左）与磁贴版（右）
using (var sheet = new Bitmap(560, 300, PixelFormat.Format32bppArgb))
{
    using var g = Graphics.FromImage(sheet);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.FromArgb(12, 15, 21));
    OrbitMark.Draw(g, new RectangleF(40, 60, 180, 180), accent, 12f);
    OrbitMark.DrawTile(g, new RectangleF(300, 60, 180, 180), accent, background);

    using var font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    using var brush = new SolidBrush(Color.FromArgb(226, 232, 242));
    g.DrawString("透明版 / 磁贴版", font, brush, 40, 20);

    // 小尺寸检查
    OrbitMark.DrawTile(g, new RectangleF(40, 250, 16, 16), accent, background);
    OrbitMark.DrawTile(g, new RectangleF(70, 244, 24, 24), accent, background);
    OrbitMark.DrawTile(g, new RectangleF(104, 236, 32, 32), accent, background);
    g.DrawString("16 / 24 / 32", font, brush, 150, 244);

    sheet.Save(Path.Combine(root, "docs", "logo.png"), ImageFormat.Png);
}

// 写 ICO（每帧用 PNG 压缩，Vista+ 支持）
var icoPath = Path.Combine(root, "app.ico");
using (var fs = File.Create(icoPath))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)frames.Count);

    var offset = 6 + (16 * frames.Count);
    for (var i = 0; i < frames.Count; i++)
    {
        var size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
    {
        w.Write(frame);
    }
}

Console.WriteLine($"saved {icoPath}");
Console.WriteLine($"saved {Path.Combine(root, "docs", "logo.png")}");
