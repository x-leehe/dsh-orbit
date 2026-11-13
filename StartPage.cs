using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 新建标签页（原生 C# 绘制，不是网页）：
/// 顶部搜索框（单引擎图标 + 下拉选择 + 搜索按钮），下面是 4×4 宫格——
/// 左上 3×3 是「打开 DSH」大入口，其余 7 格是访问记录；支持自定义背景图。
/// </summary>
internal sealed class StartPage : Control
{
    private const int Pad = 28;
    private const int SearchHeight = 46;
    private const int GridGap = 12;
    private const int EngineSize = 28;
    private const int SearchButtonSize = 28;

    private static readonly Color BackTop = Color.FromArgb(15, 19, 27);
    private static readonly Color BackBottom = Color.FromArgb(11, 14, 20);
    private static readonly Color TileBack = Color.FromArgb(23, 29, 41);
    private static readonly Color TileHover = Color.FromArgb(31, 39, 54);
    private static readonly Color TileBorder = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color TextPrimary = Color.FromArgb(232, 238, 246);
    private static readonly Color TextMuted = Color.FromArgb(143, 160, 182);
    private static readonly Color TextDim = Color.FromArgb(110, 124, 145);
    /// <summary>DSH 的品牌蓝，比 #7AAAFF 略深一点。</summary>
    private static readonly Color HeroBlue = Color.FromArgb(110, 156, 240);
    private static readonly Color HeroBlueHover = Color.FromArgb(140, 184, 255);
    private static readonly Color AccentBlue = Color.FromArgb(86, 134, 254);

    private readonly ShellState _state;
    private readonly TextBox _search;
    private readonly Font _fontHero;
    private readonly Font _fontSub;
    private readonly Font _fontTile;

    private Rectangle _searchRect;
    private Rectangle _engineRect;
    private Rectangle _searchButtonRect;
    private Rectangle _bigTile;
    private readonly List<(Rectangle Bounds, HistoryEntry Entry)> _tileHits = new();
    private readonly List<Rectangle> _emptySlots = new();
    private readonly List<(Rectangle Bounds, HistoryEntry Entry)> _deleteHits = new();

    private Image? _background;
    private string _backgroundLoadedFrom = "";

    private string? _hover;

    public StartPage(ShellState state, string dshUrl)
    {
        _state = state;
        DshUrl = dshUrl;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = BackTop;

        var body = Ui.PickFamily("HarmonyOS Sans SC", Ui.PickFamily("HarmonyOS Sans", "Segoe UI"));
        var medium = Ui.PickFamily("HarmonyOS Sans SC Medium", body);
        _fontHero = new Font(medium, 78f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontSub = new Font(body, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontTile = new Font(body, 12f, FontStyle.Regular, GraphicsUnit.Pixel);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(26, 33, 45),
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 11f),
            PlaceholderText = "搜索，或输入网址后回车",
        };
        _search.KeyDown += OnSearchKeyDown;
        Controls.Add(_search);

        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(24, 30, 41),
            ForeColor = TextPrimary,
            ShowImageMargin = false,
        };
        menu.Items.Add("清空访问记录", null, (_, _) => ClearHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("选择背景图片…", null, (_, _) => PickBackground());
        menu.Items.Add("清除背景", null, (_, _) => SetBackground(null));
        ContextMenuStrip = menu;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string DshUrl { get; set; }

    public event Action<string>? NavigateRequested;

    public event Action? AddEngineRequested;

    public event Action<string>? EngineChanged;

    public event Action<string?>? BackgroundChanged;

    /// <summary>访问记录被删除（外层负责落盘）。</summary>
    public event Action? HistoryChanged;

    public void DeleteHistory(HistoryEntry entry)
    {
        _state.History.RemoveAll(h =>
            string.Equals(h.Url, entry.Url, StringComparison.OrdinalIgnoreCase));
        HistoryChanged?.Invoke();
        LayoutPage();
        Invalidate();
    }

    public void ClearHistory()
    {
        if (_state.History.Count == 0)
        {
            return;
        }

        _state.History.Clear();
        HistoryChanged?.Invoke();
        LayoutPage();
        Invalidate();
    }

    /// <summary>数据变了（引擎/记录/背景），重画一帧。</summary>
    public void RefreshContent() => Invalidate();

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    private SearchEngine ActiveEngine =>
        _state.Engines.FirstOrDefault(e => e.Name == _state.ActiveEngine) ?? _state.Engines[0];

    private string DshAuthority =>
        Uri.TryCreate(DshUrl, UriKind.Absolute, out var uri) ? uri.Authority : DshUrl;

    // ---------------------------------------------------------------- 布局

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutPage();
    }

    private void LayoutPage()
    {
        var width = Width;
        var height = Height;
        if (width < 200 || height < 200)
        {
            return;
        }

        var contentWidth = Math.Min(980, width - (Pad * 2));
        var left = (width - contentWidth) / 2;
        _searchRect = new Rectangle(left, Pad, contentWidth, SearchHeight);

        _engineRect = new Rectangle(_searchRect.X + 9, _searchRect.Y + ((SearchHeight - EngineSize) / 2), EngineSize, EngineSize);
        _searchButtonRect = new Rectangle(
            _searchRect.Right - 9 - SearchButtonSize,
            _searchRect.Y + ((SearchHeight - SearchButtonSize) / 2),
            SearchButtonSize,
            SearchButtonSize);

        var textLeft = _engineRect.Right + 18;
        var textRight = _searchButtonRect.Left - 8;
        _search.Bounds = new Rectangle(
            textLeft,
            _searchRect.Y + ((SearchHeight - _search.PreferredHeight) / 2),
            Math.Max(80, textRight - textLeft),
            _search.PreferredHeight);

        var gridTop = _searchRect.Bottom + 22;
        var gridHeight = height - gridTop - Pad;
        if (gridHeight < 220)
        {
            gridHeight = Math.Max(0, height - gridTop - 12);
        }

        var cellW = (contentWidth - (3 * GridGap)) / 4f;
        var cellH = (gridHeight - (3 * GridGap)) / 4f;
        float CellX(int c) => left + (c * (cellW + GridGap));
        float CellY(int r) => gridTop + (r * (cellH + GridGap));

        _bigTile = new Rectangle(
            (int)CellX(0), (int)CellY(0),
            (int)((3 * cellW) + (2 * GridGap)), (int)((3 * cellH) + (2 * GridGap)));

        _tileHits.Clear();
        _emptySlots.Clear();
        _deleteHits.Clear();

        var cells = new List<Rectangle>
        {
            new((int)CellX(3), (int)CellY(0), (int)cellW, (int)cellH),
            new((int)CellX(3), (int)CellY(1), (int)cellW, (int)cellH),
            new((int)CellX(3), (int)CellY(2), (int)cellW, (int)cellH),
            new((int)CellX(0), (int)CellY(3), (int)cellW, (int)cellH),
            new((int)CellX(1), (int)CellY(3), (int)cellW, (int)cellH),
            new((int)CellX(2), (int)CellY(3), (int)cellW, (int)cellH),
            new((int)CellX(3), (int)CellY(3), (int)cellW, (int)cellH),
        };

        var history = RecentHistory();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i < history.Count)
            {
                _tileHits.Add((cells[i], history[i]));
            }
            else
            {
                _emptySlots.Add(cells[i]);
            }
        }
    }

    /// <summary>最近访问：去掉 DSH 自己的地址（它只应该出现在「打开 DSH」里）。</summary>
    private List<HistoryEntry> RecentHistory()
    {
        var authority = DshAuthority;
        return _state.History
            .Where(h => !string.IsNullOrWhiteSpace(h.Url))
            .Where(h => !IsDshUrl(h.Url, authority))
            .GroupBy(h => h.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.VisitedAt).First())
            .OrderByDescending(h => h.VisitedAt)
            .Take(7)
            .ToList();
    }

    private static bool IsDshUrl(string url, string authority) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        DrawBackground(g);
        DrawSearch(g);
        DrawBigTile(g);
        DrawHistoryTiles(g);
    }

    private void DrawBackground(Graphics g)
    {
        if (_backgroundLoadedFrom != (_state.BackgroundPath ?? string.Empty))
        {
            LoadBackground();
        }

        if (_background is null)
        {
            using var back = new LinearGradientBrush(ClientRectangle, BackTop, BackBottom, 90f);
            g.FillRectangle(back, ClientRectangle);
            return;
        }

        var scale = Math.Max(Width / (float)_background.Width, Height / (float)_background.Height);
        var w = (int)(_background.Width * scale);
        var h = (int)(_background.Height * scale);
        g.DrawImage(_background, (Width - w) / 2, (Height - h) / 2, w, h);

        using var overlay = new SolidBrush(Color.FromArgb(150, 10, 13, 20));
        g.FillRectangle(overlay, ClientRectangle);
    }

    private void LoadBackground()
    {
        _background?.Dispose();
        _background = null;
        _backgroundLoadedFrom = _state.BackgroundPath ?? string.Empty;

        var path = _state.BackgroundPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var temp = Image.FromStream(stream);
            _background = new Bitmap(temp);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"加载自定义背景失败：{ex.Message}");
        }
    }

    private void DrawSearch(Graphics g)
    {
        using (var path = RoundRect(new RectangleF(_searchRect.X, _searchRect.Y, _searchRect.Width - 1, _searchRect.Height - 1), 12))
        {
            using var fill = new SolidBrush(Color.FromArgb(26, 33, 45));
            g.FillPath(fill, path);
            using var border = new Pen(_hover == "engine" ? Color.FromArgb(70, AccentBlue) : Color.FromArgb(34, 255, 255, 255));
            g.DrawPath(border, path);
        }

        // 注意：图标是缓存的，不能 using 释放，否则第二次绘制就抛 "Parameter is not valid"。
        var engine = ActiveEngine;
        var icon = LoadEngineIcon(engine.Icon);
        if (icon is not null)
        {
            g.DrawImage(icon, _engineRect);
        }
        else
        {
            DrawLetterBadge(g, _engineRect, engine.Name);
        }

        Icons.Draw(g, "chevron-down",
            new RectangleF(_engineRect.Right + 2, _engineRect.Y + 10, 10, 10),
            Color.FromArgb(120, 133, 152), 2f);

        var searchHover = _hover == "search";
        if (searchHover)
        {
            using var ring = RoundRect(new RectangleF(_searchButtonRect.X - 2, _searchButtonRect.Y - 2, _searchButtonRect.Width + 4, _searchButtonRect.Height + 4), 8);
            using var fill = new SolidBrush(Color.FromArgb(40, AccentBlue));
            g.FillPath(fill, ring);
        }

        Icons.Draw(g, "search",
            new RectangleF(_searchButtonRect.X + 5, _searchButtonRect.Y + 5, 18, 18),
            searchHover ? TextPrimary : TextMuted, 2f);
    }

    private void DrawBigTile(Graphics g)
    {
        var hovered = _hover == "big";

        using (var path = RoundRect(new RectangleF(_bigTile.X, _bigTile.Y, _bigTile.Width - 1, _bigTile.Height - 1), 18))
        {
            using var fill = new LinearGradientBrush(
                _bigTile,
                hovered ? Color.FromArgb(36, 46, 64) : Color.FromArgb(28, 36, 50),
                Color.FromArgb(20, 26, 37),
                90f);
            g.FillPath(fill, path);

            using var border = new Pen(hovered ? Color.FromArgb(130, AccentBlue) : Color.FromArgb(40, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        var glowRect = new RectangleF(
            _bigTile.X + (_bigTile.Width * 0.12f),
            _bigTile.Y + (_bigTile.Height * 0.18f),
            _bigTile.Width * 0.52f,
            _bigTile.Height * 0.52f);
        using (var glow = new PathGradientBrush(RoundRect(glowRect, 120)))
        {
            glow.CenterColor = Color.FromArgb(26, AccentBlue);
            glow.SurroundColors = new[] { Color.FromArgb(0, AccentBlue) };
            g.FillPath(glow, RoundRect(glowRect, 120));
        }

        var size = g.MeasureString("打开", _fontHero);
        var size2 = g.MeasureString("DSH", _fontHero);
        var blockHeight = size.Height + size2.Height - 16;
        var top = _bigTile.Y + ((_bigTile.Height - blockHeight) / 2f) - 8;

        Ui.Text(g, "打开", _fontHero, hovered ? Color.White : Color.FromArgb(238, 243, 250),
            new RectangleF(_bigTile.X, top, _bigTile.Width, size.Height), Ui.Center);
        Ui.Text(g, "DSH", _fontHero, hovered ? HeroBlueHover : HeroBlue,
            new RectangleF(_bigTile.X, top + size.Height - 16, _bigTile.Width, size2.Height), Ui.Center);

        Ui.Text(g, $"{DshAuthority}　·　单击进入", _fontSub, TextDim,
            new RectangleF(_bigTile.X, _bigTile.Bottom - 46, _bigTile.Width, 20), Ui.Center);
    }

    private void DrawHistoryTiles(Graphics g)
    {
        foreach (var (bounds, entry) in _tileHits)
        {
            var hovered = _hover == entry.Url;
            using (var path = RoundRect(new RectangleF(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), 14))
            {
                using var fill = new SolidBrush(hovered ? TileHover : TileBack);
                g.FillPath(fill, path);
                using var border = new Pen(hovered ? Color.FromArgb(90, AccentBlue) : TileBorder);
                g.DrawPath(border, path);
            }

            var badge = new Rectangle(bounds.X + 14, bounds.Y + 14, 26, 26);
            DrawLetterBadge(g, badge, entry.Title);

            if (hovered)
            {
                var deleteRect = new Rectangle(bounds.Right - 30, bounds.Y + 10, 20, 20);
                using (var path = RoundRect(new RectangleF(deleteRect.X, deleteRect.Y, deleteRect.Width, deleteRect.Height), 6))
                {
                    using var fill = new SolidBrush(Color.FromArgb(36, 240, 90, 90));
                    g.FillPath(fill, path);
                }

                Icons.Draw(g, "x",
                    new RectangleF(deleteRect.X + 4, deleteRect.Y + 4, 12, 12),
                    Color.FromArgb(240, 140, 140), 2f);
                _deleteHits.Add((deleteRect, entry));
            }

            var textLeft = badge.Right + 10;
            var textWidth = bounds.Right - textLeft - 14;
            Ui.Text(g, string.IsNullOrWhiteSpace(entry.Title) ? HostOf(entry.Url) : entry.Title, _fontTile, TextPrimary,
                new RectangleF(textLeft, bounds.Y + 12, textWidth, 20), Ui.LeftEllipsis);
            Ui.Text(g, HostOf(entry.Url), _fontSub, TextDim,
                new RectangleF(textLeft, bounds.Y + 32, textWidth, 18), Ui.LeftEllipsis);
        }

        foreach (var slot in _emptySlots)
        {
            using var path = RoundRect(new RectangleF(slot.X, slot.Y, slot.Width - 1, slot.Height - 1), 14);
            using (var fill = new SolidBrush(Color.FromArgb(72, 18, 23, 32)))
            {
                g.FillPath(fill, path);
            }

            using var pen = new Pen(Color.FromArgb(34, 255, 255, 255)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, path);
        }


    }

    private void DrawLetterBadge(Graphics g, Rectangle bounds, string text)
    {
        var letter = string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[0].ToString();
        var hue = Math.Abs(text?.GetHashCode() ?? 0) % 360;
        var color = HsvToRgb(hue, 0.55, 0.85);

        using (var path = RoundRect(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), 8))
        {
            using var fill = new SolidBrush(Color.FromArgb(40, color));
            g.FillPath(fill, path);
        }

        using var font = new Font(Ui.PickFamily("HarmonyOS Sans SC Medium", "Segoe UI"), 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        Ui.Text(g, letter, font, color, bounds, Ui.Center);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(
            (int)((r + m) * 255),
            (int)((g + m) * 255),
            (int)((b + m) * 255));
    }

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitTest(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover is null ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_engineRect.Contains(e.Location))
        {
            ShowEngineMenu();
            return;
        }

        if (_searchButtonRect.Contains(e.Location))
        {
            RunSearch();
            return;
        }

        foreach (var (bounds, entry) in _deleteHits)
        {
            if (bounds.Contains(e.Location))
            {
                DeleteHistory(entry);
                return;
            }
        }

        if (_bigTile.Contains(e.Location))
        {
            NavigateRequested?.Invoke(DshUrl);
            return;
        }

        foreach (var (bounds, entry) in _tileHits)
        {
            if (bounds.Contains(e.Location))
            {
                NavigateRequested?.Invoke(entry.Url);
                return;
            }
        }
    }

    private string? HitTest(Point point)
    {
        if (_engineRect.Contains(point))
        {
            return "engine";
        }

        if (_searchButtonRect.Contains(point))
        {
            return "search";
        }

        if (_bigTile.Contains(point))
        {
            return "big";
        }

        foreach (var (bounds, entry) in _tileHits)
        {
            if (bounds.Contains(point))
            {
                return entry.Url;
            }
        }

        return null;
    }

    private void ShowEngineMenu()
    {
        var origin = PointToScreen(new Point(_engineRect.Left - 6, _searchRect.Bottom + 6));

        // 鼠标事件处理完再弹，否则激活会立刻被还给主窗口、下拉随即 Deactivate 关闭。
        BeginInvoke(() =>
        {
            var menu = new EngineMenu(_state.Engines, _state.ActiveEngine, origin);
            menu.SelectedEngine += engine =>
            {
                _state.ActiveEngine = engine.Name;
                EngineChanged?.Invoke(engine.Name);
                Invalidate();
            };
            menu.AddEngine += () => AddEngineRequested?.Invoke();
            menu.Show(this);
            menu.Activate();
        });
    }

    private void RunSearch()
    {
        var text = _search.Text.Trim();
        if (text.Length == 0)
        {
            _search.Focus();
            return;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            NavigateRequested?.Invoke(uri.ToString());
            return;
        }

        if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate($"https://{text}", UriKind.Absolute, out var bare))
        {
            NavigateRequested?.Invoke(bare.ToString());
            return;
        }

        var engine = ActiveEngine;
        NavigateRequested?.Invoke(engine.Template.Replace("{q}", Uri.EscapeDataString(text)));
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.SuppressKeyPress = true;
        RunSearch();
    }

    private void PickBackground()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetBackground(dialog.FileName);
    }

    private void SetBackground(string? path)
    {
        _state.BackgroundPath = path;
        LoadBackground();
        BackgroundChanged?.Invoke(path);
        Invalidate();
    }

    // ---------------------------------------------------------------- 资源

    private static readonly Dictionary<string, Image?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static Image? LoadEngineIcon(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (IconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Image? image = null;
        try
        {
            var assembly = typeof(StartPage).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"engines.{key}.png", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    using var temp = Image.FromStream(stream);
                    image = new Bitmap(temp);
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"加载搜索引擎图标失败（{key}）：{ex.Message}");
        }

        IconCache[key] = image;
        return image;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    private static GraphicsPath RoundRect(RectangleF rect, float radius)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontHero.Dispose();
            _fontSub.Dispose();
            _fontTile.Dispose();
            _search.Dispose();
            _background?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>搜索引擎下拉：列出所有引擎 + 「添加搜索引擎…」。</summary>
internal sealed class EngineMenu : Form
{
    private const int RowHeight = 36;
    private const int MenuWidth = 208;

    private static readonly Color Back = Color.FromArgb(24, 30, 41);
    private static readonly Color TextPrimary = Color.FromArgb(232, 238, 246);
    private static readonly Color TextMuted = Color.FromArgb(143, 160, 182);

    private readonly IReadOnlyList<SearchEngine> _engines;
    private readonly string _active;
    private readonly int _addTop;
    private int _hover = -1;

    public EngineMenu(IReadOnlyList<SearchEngine> engines, string active, Point origin)
    {
        _engines = engines;
        _active = active;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Back;
        DoubleBuffered = true;
        ClientSize = new Size(MenuWidth, 8 + (engines.Count * RowHeight) + 10 + (RowHeight - 4) + 8);
        _addTop = 8 + (engines.Count * RowHeight) + 4;

        Location = origin;
        var shownAt = DateTime.UtcNow;
        Deactivate += (_, _) =>
        {
            // 刚弹出的几十毫秒内可能收到一次假失活，忽略掉。
            if ((DateTime.UtcNow - shownAt).TotalMilliseconds > 200)
            {
                Close();
            }
        };
    }

    public event Action<SearchEngine>? SelectedEngine;

    public event Action? AddEngine;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        g.Clear(Back);
        using (var border = new Pen(Color.FromArgb(52, 62, 78)))
        {
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        using var font = new Font(Ui.PickFamily("HarmonyOS Sans SC", "Segoe UI"), 12f, FontStyle.Regular, GraphicsUnit.Pixel);

        for (var i = 0; i < _engines.Count; i++)
        {
            var engine = _engines[i];
            var row = new Rectangle(6, 6 + (i * RowHeight), ClientSize.Width - 12, RowHeight - 4);
            if (_hover == i)
            {
                using var path = Rounded(row, 8);
                using var fill = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
                g.FillPath(fill, path);
            }

            var iconRect = new Rectangle(row.X + 8, row.Y + ((row.Height - 20) / 2), 20, 20);
            DrawEngineIcon(g, engine, iconRect);

            var active = engine.Name == _active;
            Ui.Text(g, engine.Name, font, active ? Color.FromArgb(122, 170, 255) : TextPrimary,
                new RectangleF(iconRect.Right + 10, row.Y, row.Width - 60, row.Height));

            if (active)
            {
                Icons.Draw(g, "check", new RectangleF(row.Right - 26, row.Y + 10, 14, 14), Color.FromArgb(122, 170, 255), 2f);
            }
        }

        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255)))
        {
            g.DrawLine(pen, 10, _addTop, ClientSize.Width - 10, _addTop);
        }

        var addRow = new Rectangle(6, _addTop + 4, ClientSize.Width - 12, RowHeight - 8);
        if (_hover == _engines.Count)
        {
            using var path = Rounded(addRow, 8);
            using var fill = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
            g.FillPath(fill, path);
        }

        Icons.Draw(g, "plus", new RectangleF(addRow.X + 10, addRow.Y + 6, 14, 14), TextMuted, 2f);
        Ui.Text(g, "添加搜索引擎…", font, TextMuted,
            new RectangleF(addRow.X + 34, addRow.Y, addRow.Width - 40, addRow.Height));
    }

    private static void DrawEngineIcon(Graphics g, SearchEngine engine, Rectangle rect)
    {
        if (!string.IsNullOrWhiteSpace(engine.Icon))
        {
            var assembly = typeof(EngineMenu).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"engines.{engine.Icon}.png", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    using var temp = Image.FromStream(stream);
                    g.DrawImage(temp, rect);
                    return;
                }
            }
        }

        using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(TextPrimary);
        var text = string.IsNullOrWhiteSpace(engine.Name) ? "?" : engine.Name.Trim()[0].ToString();
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, rect.X + ((rect.Width - size.Width) / 2), rect.Y + ((rect.Height - size.Height) / 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = -1;
        if (e.Y >= 6 && e.Y < 6 + (_engines.Count * RowHeight))
        {
            index = (e.Y - 6) / RowHeight;
        }
        else if (e.Y >= _addTop + 4)
        {
            index = _engines.Count;
        }

        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_hover >= 0 && _hover < _engines.Count)
        {
            var engine = _engines[_hover];
            Close();
            SelectedEngine?.Invoke(engine);
        }
        else if (_hover == _engines.Count)
        {
            Close();
            AddEngine?.Invoke();
        }
    }

    private static GraphicsPath Rounded(Rectangle rect, float radius)
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
