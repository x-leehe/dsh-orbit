using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 新建标签页（原生 C# 绘制，不是网页）：
/// 顶部搜索框（单引擎图标 + 下拉选择 + 搜索按钮），下面 4×4 宫格——
/// 左上 2×2 是「打开 DSH」主磁贴，其余 12 格是访问记录；支持自定义背景图。
/// 设计语言：WinUI 3（Fluent）+ Material 3 磁贴，见 docs/design-fluent.md §4.4。
/// </summary>
internal sealed class StartPage : Control
{
    private const int PagePad = 32;
    private const int SearchHeight = 40;
    private const int SearchMaxWidth = 760;
    private const int ContentMaxWidth = 980;
    private const int GridGap = 12;
    private const int HistorySlots = 12;
    private const string SearchPlaceholder = "搜索，或输入网址后回车";

    private readonly ShellState _state;
    private readonly TextBox _search;
    private readonly ContextMenuStrip _menu;

    private Rectangle _searchRect;
    private Rectangle _engineIconRect;
    private Rectangle _chevronRect;
    private Rectangle _engineHit;
    private Rectangle _searchButtonRect;
    private Rectangle _bigTile;

    private readonly List<(Rectangle Bounds, HistoryEntry Entry)> _tileHits = new();
    private readonly List<Rectangle> _emptySlots = new();
    private readonly List<(Rectangle Bounds, HistoryEntry Entry)> _deleteHits = new();

    private Image? _background;
    private string _backgroundLoadedFrom = "";
    private float _scale = 1f;
    private string? _hover;
    private string? _hoverDelete;
    private bool _fieldHovered;
    private bool _showingPlaceholder;

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
        BackColor = Fluent.Mica;
        _scale = Math.Clamp(DeviceDpi / 96f, 0.5f, 4f);

        // 原生输入框只负责收字；外框、图标由 OnPaint 画。
        // 占位文字直接写字入 TextBox（系统 cue banner 在离屏渲染里不打印，且颜色不可控）。
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Fluent.ControlFill,
            ForeColor = Fluent.TextTertiary,
            Font = Fluent.Font(TextRole.Body, false, _scale),
            TabStop = true,
        };
        _search.KeyDown += OnSearchKeyDown;
        _search.TextChanged += OnSearchTextChanged;
        _search.MouseEnter += (_, _) => SetFieldHovered(true);
        _search.MouseLeave += (_, _) => SetFieldHovered(false);
        _search.Enter += (_, _) =>
        {
            HidePlaceholder();
            Invalidate();
        };
        _search.Leave += (_, _) =>
        {
            if (!_showingPlaceholder && _search.Text.Length == 0)
            {
                ShowPlaceholder();
            }

            Invalidate();
        };
        Controls.Add(_search);

        _menu = BuildContextMenu();
        ContextMenuStrip = _menu;
        Fluent.Changed += OnFluentChanged;

        ShowPlaceholder();
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
        HidePlaceholder();
        _search.SelectAll();
    }

    private SearchEngine ActiveEngine =>
        _state.Engines.FirstOrDefault(e => e.Name == _state.ActiveEngine) ?? _state.Engines[0];

    private string DshAuthority =>
        Uri.TryCreate(DshUrl, UriKind.Absolute, out var uri) ? uri.Authority : DshUrl;

    // ---------------------------------------------------------------- 占位文字

    private void ShowPlaceholder()
    {
        _showingPlaceholder = true;
        _search.ForeColor = Fluent.TextTertiary;
        if (_search.Text != SearchPlaceholder)
        {
            _search.Text = SearchPlaceholder;
        }
    }

    private void HidePlaceholder()
    {
        if (!_showingPlaceholder)
        {
            _search.ForeColor = Fluent.TextPrimary;
            return;
        }

        _showingPlaceholder = false;
        _search.ForeColor = Fluent.TextPrimary;
        if (_search.Text == SearchPlaceholder)
        {
            _search.Text = string.Empty;
        }
    }

    private void OnSearchTextChanged(object? sender, EventArgs e)
    {
        if (_showingPlaceholder)
        {
            // 用户在占位状态下改动文本（正常不会发生：聚焦时占位已清空）。
            if (_search.Text != SearchPlaceholder)
            {
                _showingPlaceholder = false;
                _search.ForeColor = Fluent.TextPrimary;
            }

            return;
        }

        if (!_search.Focused && _search.Text.Length == 0)
        {
            ShowPlaceholder();
        }
    }

    // ---------------------------------------------------------------- 缩放 / 布局

    private int S(float value) => (int)Math.Round(value * _scale);

    private static RectangleF RectF(Rectangle rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _scale = Math.Clamp(DeviceDpi / 96f, 0.5f, 4f);
        LayoutPage();
    }

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

        _scale = Math.Clamp(DeviceDpi / 96f, 0.5f, 4f);
        ApplySearchStyle();

        var pad = S(PagePad);
        var contentWidth = Math.Min(S(ContentMaxWidth), width - (2 * pad));
        var contentLeft = (width - contentWidth) / 2;

        // ---- 搜索框：居中、最大 760 ----
        var searchWidth = Math.Min(S(SearchMaxWidth), contentWidth);
        var searchLeft = (width - searchWidth) / 2;
        _searchRect = new Rectangle(searchLeft, S(32), searchWidth, S(SearchHeight));

        var iconSize = S(20);
        _engineIconRect = new Rectangle(
            _searchRect.X + S(12),
            _searchRect.Y + ((_searchRect.Height - iconSize) / 2),
            iconSize,
            iconSize);

        var chevron = S(12);
        _chevronRect = new Rectangle(
            _engineIconRect.Right + S(4),
            _searchRect.Y + ((_searchRect.Height - chevron) / 2),
            chevron,
            chevron);

        _engineHit = Rectangle.FromLTRB(
            _engineIconRect.Left - S(4),
            _searchRect.Y + S(6),
            _chevronRect.Right + S(4),
            _searchRect.Bottom - S(6));

        var buttonSize = S(32);
        _searchButtonRect = new Rectangle(
            _searchRect.Right - S(4) - buttonSize,
            _searchRect.Y + ((_searchRect.Height - buttonSize) / 2),
            buttonSize,
            buttonSize);

        var textLeft = _chevronRect.Right + S(12);
        var textRight = _searchButtonRect.Left - S(8);
        var textWidth = Math.Max(S(80), textRight - textLeft);
        _search.Bounds = new Rectangle(
            textLeft,
            _searchRect.Y + ((_searchRect.Height - _search.PreferredHeight) / 2),
            textWidth,
            _search.PreferredHeight);

        // ---- 4×4 宫格：主磁贴 2×2，其余 12 格是访问记录 ----
        var gap = S(GridGap);
        var gridTop = _searchRect.Bottom + S(24);
        var available = height - gridTop - pad;
        var gridHeight = Math.Min(available, S(660));
        if (gridHeight < S(220))
        {
            gridHeight = Math.Max(0, height - gridTop - S(12));
        }

        // 行列统一切分：每行等高，主磁贴高度 = 2 行 + 1 个行距。
        var cellWidth = (contentWidth - (3f * gap)) / 4f;
        var cellHeight = (gridHeight - (3f * gap)) / 4f;

        int ColumnX(int column) => contentLeft + (int)Math.Round(column * (cellWidth + gap));
        int RowY(int row) => gridTop + (int)Math.Round(row * (cellHeight + gap));
        var colWidth = (int)Math.Round(cellWidth);
        var rowHeight = (int)Math.Round(cellHeight);

        Rectangle Cell(int column, int row) => new(ColumnX(column), RowY(row), colWidth, rowHeight);

        _bigTile = new Rectangle(
            contentLeft,
            gridTop,
            ColumnX(2) - contentLeft - gap,
            RowY(2) - gridTop - gap);

        var cells = new List<Rectangle>
        {
            Cell(2, 0), Cell(3, 0), Cell(2, 1), Cell(3, 1),
            Cell(0, 2), Cell(1, 2), Cell(2, 2), Cell(3, 2),
            Cell(0, 3), Cell(1, 3), Cell(2, 3), Cell(3, 3),
        };

        _tileHits.Clear();
        _emptySlots.Clear();
        _deleteHits.Clear();

        var history = RecentHistory();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i < history.Count)
            {
                var bounds = cells[i];
                _tileHits.Add((bounds, history[i]));
                _deleteHits.Add((DeleteButtonRect(bounds), history[i]));
            }
            else
            {
                _emptySlots.Add(cells[i]);
            }
        }

        ApplySearchStyle();
    }

    private Rectangle DeleteButtonRect(Rectangle tile)
    {
        var pad = S(16);
        var size = S(20);
        return new Rectangle(tile.Right - pad - size, tile.Y + pad, size, size);
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
            .Take(HistorySlots)
            .ToList();
    }

    private static bool IsDshUrl(string url, string authority) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        DrawBackground(g);
        if (_searchRect.Width <= 0)
        {
            return;
        }

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
            using var back = new LinearGradientBrush(
                ClientRectangle,
                Fluent.Mica,
                Fluent.Mix(Fluent.Mica, Fluent.AccentFill, 0.05f),
                90f);
            g.FillRectangle(back, ClientRectangle);
            return;
        }

        var scale = Math.Max(Width / (float)_background.Width, Height / (float)_background.Height);
        var w = (int)(_background.Width * scale);
        var h = (int)(_background.Height * scale);
        g.DrawImage(_background, (Width - w) / 2, (Height - h) / 2, w, h);

        // 自定义背景图之上压一层 Mica 遮罩，保证文字对比度。
        using var overlay = new SolidBrush(
            Fluent.WithAlpha(Fluent.Mica, Fluent.IsDark ? 0.62f : 0.50f));
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
        var rect = RectF(_searchRect);

        // WinUI TextBox 外框（4px 圆角、ControlFill、聚焦强调色下划线）；
        // 文本由原生 TextBox 负责，所以这里不传文字。
        Fluent.TextBox(g, rect, null, null, _search.Focused, _fieldHovered, _scale);

        // 左：当前引擎图标 20 + chevron-down
        if (_hover == "engine")
        {
            using var backplate = Fluent.Rounded(RectF(_engineHit), Fluent.RControl * _scale);
            Fluent.Fill(g, backplate, Fluent.HoverLayer);
        }

        EngineArt.Draw(g, ActiveEngine, RectF(_engineIconRect), _scale, S(6));
        Icons.Draw(g, "chevron-down", RectF(_chevronRect), Fluent.TextTertiary, 2f);

        // 右：32×32 搜索图标按钮
        Fluent.IconButton(
            g,
            RectF(_searchButtonRect),
            "search",
            _hover == "search" ? VisualState.Hover : VisualState.Rest,
            S(16),
            _hover == "search" ? Fluent.TextPrimary : Fluent.TextSecondary);
    }

    private void DrawBigTile(Graphics g)
    {
        var rect = RectF(_bigTile);
        var radius = Fluent.RCard * _scale;
        var hovered = _hover == "big";
        var fill = hovered
            ? Fluent.Mix(Fluent.Layer, Fluent.AccentFill, 0.06f)
            : Fluent.Layer;

        if (hovered)
        {
            // 悬停：6% 强调色状态层 + 海拔 2
            Fluent.Shadow(g, rect, radius, 2);
            Fluent.Card(g, rect, radius, 0, fill);
        }
        else
        {
            Fluent.Card(g, rect, radius, 2, fill);
        }

        var pad = S(24);
        var left = rect.X + pad;
        var textWidth = Math.Max(0f, rect.Width - (2 * pad));

        var lineHeight = S(16);
        var titleHeight = S(36);
        var gap = S(4);
        var hintGap = S(24);
        var blockHeight = lineHeight + gap + titleHeight + gap + lineHeight + hintGap + lineHeight;

        // 内容整体垂直居中、略偏上，避免卡片下半部出现大片空洞。
        var top = rect.Y + Math.Max((float)pad, ((rect.Height - blockHeight) / 2f) - S(8));

        Fluent.Text(g, "DSH WEB", TextRole.Caption, true, Fluent.TextTertiary,
            new RectangleF(left, top, textWidth, lineHeight),
            StringAlignment.Near, StringTrimming.None, _scale);
        top += lineHeight + gap;

        Fluent.Text(g, "打开 DSH", TextRole.Title, true, Fluent.TextPrimary,
            new RectangleF(left, top, textWidth, titleHeight),
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);
        top += titleHeight + gap;

        Fluent.Text(g, DshAuthority, TextRole.Caption, false, Fluent.TextSecondary,
            new RectangleF(left, top, textWidth, lineHeight),
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);
        top += lineHeight + hintGap;

        Fluent.Text(g, "单击进入 →", TextRole.Caption, true, Fluent.AccentText,
            new RectangleF(left, top, textWidth, lineHeight),
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);
    }

    private void DrawHistoryTiles(Graphics g)
    {
        var pad = S(16);
        var badgeSize = S(28);

        foreach (var (bounds, entry) in _tileHits)
        {
            var rect = RectF(bounds);
            var hovered = _hover == entry.Url;

            // M3 磁贴：16px 圆角、悬停抬升一级
            Fluent.Tile(g, rect, hovered ? VisualState.Hover : VisualState.Rest, Fluent.RTile * _scale);

            var badge = new RectangleF(rect.X + pad, rect.Y + pad, badgeSize, badgeSize);
            EngineArt.DrawLetter(g, badge, TitleOf(entry), _scale, S(8));

            var textLeft = rect.X + pad;
            var textWidth = Math.Max(0f, rect.Width - (2 * pad));
            var titleTop = badge.Bottom + S(12);

            Fluent.Text(g, TitleOf(entry), TextRole.Body, false, Fluent.TextPrimary,
                new RectangleF(textLeft, titleTop, textWidth, S(20)),
                StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);

            Fluent.Text(g, HostOf(entry.Url), TextRole.Caption, false, Fluent.TextTertiary,
                new RectangleF(textLeft, titleTop + S(20), textWidth, S(16)),
                StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);
        }

        // 悬停的磁贴右上角显示 20×20 删除按钮
        foreach (var (deleteRect, entry) in _deleteHits)
        {
            if (_hover != entry.Url)
            {
                continue;
            }

            var active = _hoverDelete == entry.Url;
            Fluent.IconButton(
                g,
                RectF(deleteRect),
                "x",
                active ? VisualState.Hover : VisualState.Rest,
                S(12),
                active ? Fluent.Critical : Fluent.TextSecondary,
                Fluent.WithAlpha(Fluent.Critical, 0.18f));
        }

        // 空槽位：StrokeColor 圆点虚线 + LayerAlt/Mica 半透明底
        foreach (var slot in _emptySlots)
        {
            var rect = RectF(slot);
            using var path = Fluent.Rounded(rect, Fluent.RTile * _scale);
            Fluent.Fill(g, path, Fluent.Mix(Fluent.LayerAlt, Fluent.Mica, 0.5f));

            var width = Math.Max(2f, _scale * 2f);
            using var pen = new Pen(Fluent.StrokeColor, width)
            {
                // GDI+ 的 Dot 图案在 2px 笔宽下就是 2px 圆点 + 4px 间距，边框一眼可辨。
                DashStyle = DashStyle.Dot,
                DashCap = DashCap.Round,
            };
            g.DrawPath(pen, path);
        }
    }

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hover = HitTest(e.Location);
        string? deleteHover = null;
        foreach (var (bounds, entry) in _deleteHits)
        {
            if (bounds.Contains(e.Location))
            {
                deleteHover = entry.Url;
                break;
            }
        }

        var field = _searchRect.Contains(e.Location);
        var dirty = false;

        if (hover != _hover)
        {
            _hover = hover;
            dirty = true;
        }

        if (deleteHover != _hoverDelete)
        {
            _hoverDelete = deleteHover;
            dirty = true;
        }

        if (field != _fieldHovered)
        {
            SetFieldHovered(field);
            dirty = true;
        }

        Cursor = hover is not null || deleteHover is not null
            ? Cursors.Hand
            : field
                ? Cursors.IBeam
                : Cursors.Default;

        if (dirty)
        {
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _hoverDelete = null;
        Cursor = Cursors.Default;
        SetFieldHovered(false);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_engineHit.Contains(e.Location))
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

        if (_searchRect.Contains(e.Location))
        {
            _search.Focus();
            return;
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
        if (_engineHit.Contains(point))
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
        var origin = PointToScreen(new Point(_searchRect.Left, _searchRect.Bottom + S(6)));

        // 鼠标事件处理完再弹，否则激活会立刻被还给主窗口、下拉随即 Deactivate 关闭。
        BeginInvoke(() =>
        {
            var menu = new EngineMenu(_state.Engines, _state.ActiveEngine, origin, _scale);
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
        var text = _showingPlaceholder ? string.Empty : _search.Text.Trim();
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

    // ---------------------------------------------------------------- 搜索框配色

    /// <summary>搜索框悬停态：鼠标在原生输入框上时也要保持（父控件的 MouseMove 到不了那里）。</summary>
    private void SetFieldHovered(bool value)
    {
        if (_fieldHovered == value)
        {
            return;
        }

        _fieldHovered = value;
        ApplySearchStyle();
        Invalidate();
    }

    private void ApplySearchStyle()
    {        var fill = _fieldHovered ? Fluent.ControlFillHover : Fluent.ControlFill;
        var font = Fluent.Font(TextRole.Body, false, _scale);

        if (!ReferenceEquals(_search.Font, font))
        {
            _search.Font = font;
        }

        if (_search.BackColor != fill)
        {
            _search.BackColor = fill;
        }

        var foreground = _showingPlaceholder ? Fluent.TextTertiary : Fluent.TextPrimary;
        if (_search.ForeColor != foreground)
        {
            _search.ForeColor = foreground;
        }
    }

    private void OnFluentChanged()
    {
        BackColor = Fluent.Mica;
        _menu.BackColor = Fluent.Flyout;
        _menu.ForeColor = Fluent.TextPrimary;
        ApplySearchStyle();
        Invalidate();
    }

    // ---------------------------------------------------------------- 右键菜单

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Fluent.Flyout,
            ForeColor = Fluent.TextPrimary,
            Font = Fluent.Font(TextRole.Body, false, _scale),
            Renderer = new FluentMenuRenderer(),
        };

        var clear = new ToolStripMenuItem("清空访问记录");
        clear.Click += (_, _) => ClearHistory();

        var pick = new ToolStripMenuItem("选择背景图片…");
        pick.Click += (_, _) => PickBackground();

        var reset = new ToolStripMenuItem("清除背景");
        reset.Click += (_, _) => SetBackground(null);

        foreach (var item in new[] { clear, pick, reset })
        {
            // Fluent MenuFlyout 的行高：正文 14 + 上下 6。
            item.Padding = new Padding(S(10), S(6), S(12), S(6));
        }

        menu.Items.Add(clear);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pick);
        menu.Items.Add(reset);
        menu.Opened += (_, _) => RoundMenu(menu);
        return menu;
    }

    /// <summary>给弹出菜单切 8px 圆角。</summary>
    private void RoundMenu(ContextMenuStrip menu)
    {
        using var path = Fluent.Rounded(
            new RectangleF(0, 0, menu.Width, menu.Height),
            Fluent.RCard * _scale);
        var previous = menu.Region;
        menu.Region = new Region(path);
        previous?.Dispose();
    }

    // ---------------------------------------------------------------- 资源

    private static string TitleOf(HistoryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Title) ? HostOf(entry.Url) : entry.Title;

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Fluent.Changed -= OnFluentChanged;
            _menu.Dispose();
            _search.Dispose();
            _background?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>搜索引擎图标：优先用嵌入的 favicon，没有就画字母徽章。</summary>
internal static class EngineArt
{
    private static readonly Dictionary<string, Image?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static Image? Load(string? key)
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

    public static void Draw(Graphics g, SearchEngine engine, RectangleF rect, float scale, float radius)
    {
        var image = Load(engine.Icon);
        if (image is not null)
        {
            g.DrawImage(image, rect);
            return;
        }

        DrawLetter(g, rect, engine.Name, scale, radius);
    }

    /// <summary>字母徽章：圆角方块 + 12 semibold 首字母。</summary>
    public static void DrawLetter(Graphics g, RectangleF rect, string source, float scale, float radius)
    {
        var letter = string.IsNullOrWhiteSpace(source)
            ? "?"
            : char.ToUpperInvariant(source.Trim()[0]).ToString();

        using var path = Fluent.Rounded(rect, radius);
        Fluent.Fill(g, path, Fluent.Mix(Fluent.LayerAlt, Fluent.AccentFill, 0.16f));
        Fluent.Text(g, letter, TextRole.Caption, true, Fluent.AccentText, rect,
            StringAlignment.Center, StringTrimming.None, scale);
    }
}

/// <summary>搜索引擎下拉：Fluent Flyout（8 圆角 + 海拔 2 + 逐像素透明阴影）。</summary>
internal sealed class EngineMenu : Form
{
    private const int CardWidthBase = 240;
    private const int RowHeightBase = 32;
    private const int PadBase = 8;
    private const int ShadowMarginBase = 20;
    private const int DividerGapBase = 6;

    private readonly IReadOnlyList<SearchEngine> _engines;
    private readonly string _active;
    private readonly float _scale;
    private readonly LayeredSurface _layered = new();
    private readonly int _margin;
    private readonly int _cardWidth;
    private readonly int _cardHeight;
    private readonly int _rowHeight;
    private readonly int _pad;
    private readonly int _dividerY;
    private readonly int _addTop;
    private readonly DateTime _shownAt;

    private Bitmap? _surface;
    private int _hover = -1;

    public EngineMenu(IReadOnlyList<SearchEngine> engines, string active, Point origin, float scale)
    {
        _engines = engines;
        _active = active;
        _scale = Math.Clamp(scale, 0.5f, 4f);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Fluent.Flyout;
        Text = "搜索引擎";
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        _margin = C(ShadowMarginBase);
        _cardWidth = C(CardWidthBase);
        _rowHeight = C(RowHeightBase);
        _pad = C(PadBase);
        _dividerY = _pad + (_engines.Count * _rowHeight) + C(DividerGapBase);
        _addTop = _dividerY + C(DividerGapBase) + 1;
        _cardHeight = _addTop + _rowHeight + _pad;

        ClientSize = new Size(_cardWidth + (2 * _margin), _cardHeight + (2 * _margin));
        Location = new Point(origin.X - _margin, origin.Y - _margin);

        _shownAt = DateTime.UtcNow;
        Deactivate += (_, _) =>
        {
            // 刚弹出的几十毫秒内可能收到一次假失活，忽略掉。
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 200)
            {
                Close();
            }
        };

        _ = Handle;
        RenderSurface();
    }

    public event Action<SearchEngine>? SelectedEngine;

    public event Action? AddEngine;

    private int C(float value) => (int)Math.Round(value * _scale);

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                                  Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
            parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e) => RenderSurface();

    private void RenderSurface()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_surface is null || _surface.Width != width || _surface.Height != height)
        {
            _surface?.Dispose();
            _surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            Fluent.AntiAlias(g);

            var state = g.Save();
            g.TranslateTransform(_margin, _margin);
            DrawCard(g);
            g.Restore(state);
        }

        if (IsHandleCreated)
        {
            _layered.Push(Handle, _surface, Left, Top, 1f);
        }
    }

    private void DrawCard(Graphics g)
    {
        var card = new RectangleF(0, 0, _cardWidth, _cardHeight);
        Fluent.Card(g, card, Fluent.RCard * _scale, 2, Fluent.Flyout);

        for (var i = 0; i < _engines.Count; i++)
        {
            DrawEngineRow(g, i);
        }

        Fluent.Divider(
            g,
            new RectangleF(_pad + C(4), _dividerY, _cardWidth - (2 * _pad) - C(8), 1));

        var addRow = new RectangleF(_pad, _addTop, _cardWidth - (2 * _pad), _rowHeight);
        Fluent.MenuRow(
            g,
            addRow,
            "plus",
            "添加搜索引擎…",
            null,
            _hover == _engines.Count ? VisualState.Hover : VisualState.Rest,
            Fluent.TextSecondary,
            false,
            _scale);
    }

    private void DrawEngineRow(Graphics g, int index)
    {
        var engine = _engines[index];
        var row = new RectangleF(_pad, _pad + (index * _rowHeight), _cardWidth - (2 * _pad), _rowHeight);

        if (_hover == index)
        {
            using var backplate = Fluent.Rounded(
                new RectangleF(row.X + C(4), row.Y + C(1), row.Width - C(8), row.Height - C(2)),
                Fluent.RControl * _scale);
            Fluent.Fill(g, backplate, Fluent.HoverLayer);
        }

        var iconSize = C(20);
        var icon = new RectangleF(
            row.X + C(10),
            row.Y + ((row.Height - iconSize) / 2f),
            iconSize,
            iconSize);
        EngineArt.Draw(g, engine, icon, _scale, C(6));

        var trailing = C(30);
        Fluent.Text(
            g,
            engine.Name,
            TextRole.Body,
            false,
            Fluent.TextPrimary,
            new RectangleF(icon.Right + C(10), row.Y, row.Right - icon.Right - C(10) - trailing, row.Height),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            _scale);

        if (string.Equals(engine.Name, _active, StringComparison.Ordinal))
        {
            var check = C(16);
            Icons.Draw(
                g,
                "check",
                new RectangleF(row.Right - C(10) - check, row.Y + ((row.Height - check) / 2f), check, check),
                Fluent.AccentText,
                2f);
        }
    }

    private int HitIndex(Point point)
    {
        var x = point.X - _margin;
        var y = point.Y - _margin;
        if (x < _pad || x >= _cardWidth - _pad)
        {
            return -1;
        }

        if (y >= _pad && y < _pad + (_engines.Count * _rowHeight))
        {
            return (y - _pad) / _rowHeight;
        }

        if (y >= _addTop && y < _addTop + _rowHeight)
        {
            return _engines.Count;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitIndex(e.Location);
        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            RenderSurface();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var index = HitIndex(e.Location);
        if (index >= 0 && index < _engines.Count)
        {
            var engine = _engines[index];
            Close();
            SelectedEngine?.Invoke(engine);
        }
        else if (index == _engines.Count)
        {
            Close();
            AddEngine?.Invoke();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surface?.Dispose();
            _layered.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>右键菜单：Fluent Flyout 配色（浮出层底 + 状态层悬停 + 8px 圆角描边）。</summary>
internal sealed class FluentMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Fluent.Flyout);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var path = Fluent.Rounded(
            new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f),
            Fluent.RCard);
        Fluent.Stroke(e.Graphics, path, Fluent.StrokeColor);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        using var path = Fluent.Rounded(
            new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2),
            Fluent.RControl);
        Fluent.Fill(e.Graphics, path, Fluent.HoverLayer);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var font = e.TextFont;
        if (font is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var color = e.Item.Enabled ? Fluent.TextPrimary : Fluent.TextDisabled;
        TextRenderer.DrawText(
            e.Graphics,
            e.Text,
            font,
            e.TextRectangle,
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) =>
        Fluent.Divider(e.Graphics, new RectangleF(8, e.Item.Height / 2f, e.Item.Width - 16, 1));
}
