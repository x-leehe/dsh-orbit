# DSH-Orbit 设计规范 · Fluent(WinUI 3) × Material 3

本文件是界面改版的唯一事实来源。任何界面改动都要先对照这里，再动代码。

设计语言取自两处，冲突时以 WinUI 3 为准：

- [WinUI 3 概览](https://learn.microsoft.com/en-us/windows/apps/winui/winui3)
- [Geometry in Windows（圆角）](https://learn.microsoft.com/en-us/windows/apps/design/style/rounded-corner)
- [Typography in Windows（版式阶梯）](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
- [Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/style/spacing)
- [Color in Windows](https://learn.microsoft.com/en-us/windows/apps/design/style/color)
- [Material 3](https://m3.material.io/)：状态层、抬升、形状阶梯、涟漪

## 1. 硬性规则

1. **界面文件里不允许出现颜色字面量**。所有颜色都从 `Fluent` 取（`Color.FromArgb` 只能出现在 `Fluent.cs`）。
2. **最小字号 12px**（WinUI 的下限）。正文 14px，卡片标题 20px，大标题 28px。
3. 所有尺寸走 **4px 网格**，常用间距 `4 / 8 / 12 / 16 / 24 / 32`。
4. 圆角只有四档：控件 `4`、卡片/浮出层 `8`、M3 磁贴 `16`、药丸 `999`。
5. 文字层级只用三级：`TextPrimary` / `TextSecondary` / `TextTertiary`，不要自造灰度。
6. 悬停/按下用 `VisualState` + `Fluent.OverState` 的 8%/10% 状态层，不要写死高亮色。
7. 强调色一律取 `Fluent.AccentFill`（跟随系统强调色），不要写死 DSH 蓝。
8. 高 DPI：所有像素值经 `S(v) = round(v * DeviceDpi / 96)` 缩放；字体用 `Fluent.Font(role, strong, dpiScale)`。

## 2. 令牌

### 2.1 颜色

| 语义 | 深色 | 浅色 |
|---|---|---|
| `Mica`（页面底） | `#202020` | `#F3F3F3` |
| `Layer`（卡片） | `#2B2B2B` | `#FBFBFB` |
| `LayerAlt` | `#323232` | `#F2F4F7` |
| `Flyout`（浮出层） | `#2C2C2C` | `#F9F9F9` |
| `ControlFill` | `#303030` | `#FDFDFD` |
| `ControlFillHover` | `#3B3B3B` | `#F6F6F6` |
| `ControlFillPressed` | `#292929` | `#EFEFEF` |
| `Stroke` / `StrokeStrong` | 7% / 13% 白 | 6% / 16% 黑 |
| `Divider` | 8% 白 | 8% 黑 |
| `TextPrimary` | `#FFFFFF` | `#E4000000` |
| `TextSecondary` | 77% 白 | 62% 黑 |
| `TextTertiary` | 54% 白 | 45% 黑 |
| `TextDisabled` | 36% 白 | 36% 黑 |
| `AccentFill` | 系统强调色 +26% 亮度 | 系统强调色 −16% 亮度 |
| `OnAccent` | 近黑 | 白 |
| `Success` / `Caution` / `Critical` | `#6CCB5F` / `#FCE100` / `#FF99A4` | `#0F7B0F` / `#9D5D00` / `#C42B1C` |

### 2.2 版式

| 角色 | 字号/行高 | 用途 |
|---|---|---|
| `Caption` | 12/16 | 标签、状态、次要信息 |
| `Body` | 14/20 | 正文、菜单项、按钮 |
| `BodyLarge` | 18/24 | 强调正文 |
| `Subtitle` | 20/28 | 卡片标题、对话框标题 |
| `Title` | 28/36 | 页面标题、端口号 |
| `Display` | 40/52 | 大号展示（新标签页主标题） |

字体族：`Segoe UI Variable Text`（正文）/ `… Text Semibold`（强调）/ `… Display`（大标题）/ `… Small`（小字）。
中文由系统字体链自动回落，无需指定。Win10 回落到 `Segoe UI`。

### 2.3 海拔（M3）

| 级别 | 用途 | 阴影 |
|---|---|---|
| 0 | 页面内元素 | 无 |
| 1 | 悬停中的磁贴、工具提示 | 6 环，5% |
| 2 | 浮出层、卡片的悬停态 | 11 环，4% |
| 3 | 桌面悬浮卡片 | 17 环，3% |

### 2.4 动效

- 悬停：83ms 进入 / 167ms 退出
- 涟漪（M3）：220ms，`EaseOut`
- 页面/标签切换：250ms，`EaseEmphasized`（≈`cubic-bezier(0.2,0,0,1)`）

## 3. 绘制原语（`Fluent.cs`）

```csharp
Fluent.Card(g, rect, radius, elevation, fill)      // 卡片：层色 + 上亮下暗 1px 描边
Fluent.Shadow(g, rect, radius, level)              // M3 抬升
Fluent.Tile(g, rect, state, radius, accentEdge)    // M3 磁贴（悬停抬升）
Fluent.Button(g, rect, label, role, state, icon)   // WinUI 按钮
Fluent.IconButton(g, rect, icon, state, size)      // 32×32 图标按钮
Fluent.Segmented(g, rect, labels, selected, hover) // 分段控件
Fluent.MenuRow(g, rect, icon, label, trailing, state)
Fluent.Pill(g, rect, text, fg, bg, dot)            // 状态药丸
Fluent.TextBox(g, rect, text, placeholder, focused, hovered)
Fluent.Divider / Fluent.HairLine
Fluent.SectionLabel(g, rect, text)
Fluent.Text(g, text, role, strong, color, rect, align, trim, scale)
Fluent.Measure(g, text, role, strong, scale)
Fluent.Mix / WithAlpha / OverState / EaseOut / EaseEmphasized
new Ripple().Begin(point) + Ripple.Draw(g, bounds, radius, tint)
```

## 4. 界面规格

### 4.1 桌面小部件（460×180 卡片，外加 16px 阴影边距）

```
┌──────────────────────────────────────────────────────────┐ 8px 圆角 · 层色 · 1px 描边 · 海拔 3
│ [◈] DSH-Orbit                              ● 运行中        │ 14 semibold + 药丸
│ 127.0.0.1:3080                                 ┌─────────┐│ Title 28/36
│ profile web · 插件 12 · 标准价                  │  停止   ││ 12 secondary
│ ───────────────────────────────────────────────│  打开   ││ 1px 分隔线
│ 运行时长        余额          今日已用          └─────────┘│ 12 tertiary
│ 02:06:22       ¥24.45        ¥0.6400                      │ 14 semibold
└──────────────────────────────────────────────────────────┘
```

- 内边距 16；右侧两枚按钮宽 92、高 32、间距 8，主操作用 `SurfaceRole.Accent`
- 状态色：运行 `Success`、启动中 `Caution`、停止 `TextTertiary`、锁定 `Pill` 三级文字底
- 悬停端口号 → `AccentText` 并加深下划线；点击打开 Web

### 4.2 托盘菜单（300 宽，外加 10px 阴影边距）

```
┌────────────────────────────────────┐ 8px 圆角 · 浮出层色 · 海拔 2
│ [↗ 打开] [■ 停止] [⟳ 重启]         │ 32px 按钮行
│ ────────────────────────────────── │
│ 位置                                │ 12 tertiary
│ [ 置底 │ 置顶 │ 普通 ]              │ 32px 分段
│ 开机启动                             │
│ [ 停用 │ 启用 │ 全自动 ]            │
│ 外观                                │
│ [ 跟随系统 │ 浅色 │ 深色 ]          │
│ ────────────────────────────────── │
│ 🔒 锁定穿透                     开  │ 36px 行
│ ▤  贴边隐藏                     开  │
│ ▣  外壳关闭时                  隐藏  │
│ ◐  隐藏挂件                         │
│ ⧉  复制地址                          │
│ ▤  打开日志                          │
│ ⏻  退出（Critical 色）               │
└────────────────────────────────────┘
```

- 行高 36、图标 16、左右内边距 12、hover 背板 4px 圆角
- 键盘：↑↓ 移动、Enter 触发、Esc 关闭
- 关闭时播放 120ms 淡出（可选）

### 4.3 浏览器外壳

```
展开（默认）：
┌───────────────────────────────────────────────────────────────┐ 48px 标题栏（Mica）
│ ◈ DSH-Orbit │[ 标签 1 ×][ 标签 2 ×][+]             ⌃   ● ● ●   │
├───────────────────────────────────────────────────────────────┤ 36px 工具条
│ [ 127.0.0.1:3080                                ⟳  ⧉ ]         │ TextBox 32px
├───────────────────────────────────────────────────────────────┤
│  WebView2 内容                                                │
└───────────────────────────────────────────────────────────────┘

收起地址栏后（工具条整行消失，网页多出 36px）：
┌───────────────────────────────────────────────────────────────┐ 48px 标题栏
│ ◈ DSH-Orbit │[ 标签 1 ×][ 标签 2 ×][+]             ⌄   ● ● ●   │
├───────────────────────────────────────────────────────────────┤
│  WebView2 内容                                                │
└───────────────────────────────────────────────────────────────┘

⌃ / ⌄ = 地址栏的收起、展开按钮（固定住在红绿灯左侧，收起后仍可见）
⟳ = 刷新   ⧉ = 在浏览器打开（两者都在地址框内部右侧）
```

- 标签：高 32、上圆角 8、激活 = `Layer` 底 + 底部 2px `AccentFill` 指示条
- 关闭按钮 14px 图标，仅悬停/激活时显示
- `+` 为 32×32 图标按钮，跟在最后一个标签后面（放不下时钉在标签区最右）
- 红绿灯：保留 macOS 语义与顺序（最小化 → 最大化 → 关闭），命中区 32×32，悬停加 8% 状态层，颜色 `Caution` / `Success` / `Critical`
- 地址框：**30 高、8 圆角**（字段是「卡片级」元素，比按钮大一档圆角才不会读成分隔带）、左右各 12 内边距、文字内边距 12；
  `刷新` `在浏览器打开` 是地址框内部的 26×26 图标按钮（8% 状态层），地址文字让出它们的宽度
- 收起按钮：`chevron-up` / `chevron-down`，收起时工具条高度 0、网页区域向上填满；状态存 `shell.json` 的 `AddressCollapsed`；`Ctrl+L` 会先展开再聚焦
- 窗口：`DWMWA_USE_IMMERSIVE_DARK_MODE` 跟随主题，圆角走 DWM

### 4.4 新标签页

- 背景：`Mica` → `Mix(Mica, AccentFill, 0.05)` 竖向渐变；自定义图片时叠加 62%（深）/ 50%（浅）`Mica` 遮罩
- 搜索框：40 高、4 圆角、`ControlFill` 底、14px 占位文字；左侧引擎图标 20 + chevron，右侧搜索图标按钮
- 主磁贴“打开 DSH”：`Card` + 海拔 2，内容左对齐 24 内边距：
  - `12 tertiary` “DSH WEB”
  - `28 semibold` “打开 DSH”
  - `12 secondary` `127.0.0.1:3080`
  - 底部 `12` 强调色 “单击进入 →”
- 历史磁贴：`Tile`（16 圆角），字母徽章 28、标题 14、域名 12 tertiary，悬停显示删除按钮
- 空槽位：虚线描边用 `Stroke`，底色 `LayerAlt` 50%
- 引擎下拉：浮出层 8 圆角，行高 32，当前项右侧 `check` 图标

### 4.5 添加引擎对话框

- `ContentDialog`：8 圆角、24 内边距、`Subtitle` 标题、输入框 `TextBox` 32 高
- 底部按钮右对齐、间距 8：取消（Standard）、添加（Accent）

## 5. 验收

- `tools/Render` 输出 **浅色/深色 × 各界面** 的截图，逐张与本文档比对
- 行为回归：拖动、贴边隐藏、缩放、吸附最大化、标签切换、托盘键盘、`--show-window`
- `README.md` 截图全部替换为新界面
