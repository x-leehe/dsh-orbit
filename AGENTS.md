# AGENTS.md

给在此仓库工作的 AI 代理（以及人类贡献者）的约定。

## 重要：这是一个 AIGC 项目

本仓库的代码、文档和图标主要由 AI 生成并迭代出来。这意味着：

- 代码可能缺少单元测试、边界处理或统一的错误策略；
- 注释和命名大体一致，但不要假设有隐藏的设计文档；
- 任何改动都必须自己跑一遍构建（见下），不要凭"看起来对"就提交。

## 技术栈

- .NET 10 SDK，WinForms（`net10.0-windows`），单文件发布；
- 目标平台只有 Windows 10/11；
- 主要依赖：`Microsoft.Web.WebView2`。

## 常用命令

```powershell
dotnet build -c Release          # 编译（必须通过）
.\build.ps1                      # 产出 dist\DSH-Orbit.exe 与 dist-lite\DSH-Orbit.exe
dotnet run --project tests/Harness -c Release     # 冒烟测试（会起隔离的 dsh 实例）
dotnet run --project tools/IconGen -c Release -- .
dotnet run --project tests/Render -c Release -- artifacts/gallery            # 全部界面 × 浅/深
dotnet run --project tests/Render -c Release -- artifacts/gallery --only menu
```

涉及图标的改动必须重新跑 `tools/IconGen`，否则 `app.ico` 会过期。

## 代码约定

- 命名空间统一 `DshOrbit`；工程文件 `DshOrbit.csproj`，产物名 `DSH-Orbit`。
- 中文注释、中文 UI 文案；标识符用英文。
- 自绘 UI 统一走 GDI+（`Graphics` + `TextRenderingHint.AntiAlias`）；
  不要用 `TextRenderer`/GDI，它的文字画到带 alpha 的位图上会出现锯齿。
- 分层窗口（挂件）改动后要验证：透明、抗锯齿、点击穿透仍然正常。
- **界面配色一律从 `Fluent` 取，禁止颜色字面量**（`Color.FromArgb` 只允许出现在 `Fluent.cs`）；
  半透明叠加用 `Fluent.WithAlpha` / `Fluent.Mix`。设计规范见 `docs/design-fluent.md`。
- 版式走 WinUI 3 的字号阶梯（`TextRole`），**最小 12px**；尺寸走 4px 网格。
- 图标：只能用 Lucide 官方几何（`tools/fetch-lucide.ps1` → `LucideData.cs`），
  禁止手绘近似 Lucide 图标；品牌 logo（DSH 鲸鱼）来自 `DshMark.cs`。
- 不要把 `bin/`、`obj/`、`dist/`、`dist-lite/` 提交进仓库。

## 设计系统（WinUI 3 × Material 3）

界面设计语言是 Fluent（WinUI 3），融合少量 Material 3 的状态层/抬升/形状。所有令牌与绘制原语在 `Fluent.cs`：

- 主题：`Fluent.Init(config.Theme)` 启动时调用；`Fluent.Refresh()` 在心跳里跟随系统主题与强调色；
  用户可在托盘菜单「外观」里强制浅色/深色（`config.Theme`）。
- 颜色：`Mica` / `Layer` / `Flyout` / `ControlFill` / `StrokeColor` / `TextPrimary…Tertiary` /
  `AccentFill` / `Success` / `Caution` / `Critical`；强调色取自系统，深浅阶自动推导。
- 字体：`Segoe UI Variable Text / Display / Small`（Win10 回落 `Segoe UI`），中文由系统字体链回落。
- 圆角只有四档：`RControl=4`、`RCard=8`、`RTile=16`、`RPill=999`。
- 原语：`Card` / `Tile` / `Button` / `IconButton` / `Segmented` / `MenuRow` / `Pill` / `TextBox` / `Divider`。
- 控件级 Fluent 包装（对话框用）：`FluentButton`、`FluentTextBox`（`FluentControls.cs`）。
- 视觉验收：`RenderPreview.exe artifacts\gallery` 渲染 **浅色/深色 × 全部界面**，逐张比对规范。

## 安装器与卸载（installer/ + Uninstall.cs）

安装器是**自研**的，不要引入 Inno Setup / NSIS / WiX / MSI：

- `installer/` 是独立工程，`DshOrbit.csproj` 里已 `Compile Remove="installer\**"`；
  否则会冒出「多个入口点」的编译错误。
- 安装器把主程序当 **embedded resource**（`payload.app`）打进自己，payload 默认
  `..\dist\DSH-Orbit.exe`——**必须用自带运行时那个 47 MB 版本**，不能用 `dist-lite`，
  不然没装 .NET 的机器装完跑不起来。CI 里有长度断言守着这一点。
- 安装器自身也是 `SelfContained + PublishSingleFile`，所以 Setup 约 94 MB（运行时 47 + payload 47）。
- 卸载逻辑在**主程序** `Uninstall.cs`（`DSH-Orbit.exe --uninstall`），安装器只把
  `UninstallString` 指过去。默认**保留**用户数据，只有交互式勾选或 `/REMOVEDATA` 才删；
  `RemoveData` 里有护栏：数据目录不能等于/包含安装目录。
- 便携模式约定：exe 同目录有 `portable.txt` **或** `data\`，`AppPaths` 就把数据写到 `data\`
  （见 `Models.cs` 的 `ResolveRoot`）。

## 可空引用

工程开启了 `<WarningsAsErrors>nullable</WarningsAsErrors>`：任何 CS86xx 可空警告都会直接编译失败，
**不要用 `#pragma warning disable` 绕过**，按下面的方式处理：

- `Tab.View` 是 `WebView2?`（新标签页还没创建 WebView2），访问内核一律走 `Tab.Core`（内部是 `View?.CoreWebView2`）；
- 事件回调里不要假设 `CoreWebView2` 已经就绪，用 `?.` 或先判空；
- 释放时用 `tab.View?.Dispose()`。

`MSB3277`（WindowsBase 版本冲突）已在 csproj 里降级为消息：它来自 WebView2 包附带的 WPF 程序集，
本项目只用 WinForms，不引用 WPF。

## 修改 WebView2 外壳时的注意点

- 窗口是"原生边框 + `WM_NCCALCSIZE` 去掉标题栏"的结构：
  改窗口样式可能破坏拖动、缩放、贴边吸附，改完要实测。
- 布局必须用 `GetClientRect` 的真实客户区，不要依赖 WinForms 缓存的 `ClientSize`。
- 自绘面板需要 `ResizeRedraw` + 双缓冲，否则缩放时会留下残影。

## 提交

- 提交信息用中文，`type: 简述`（`feat` / `fix` / `refactor` / `docs` / `chore`）。
- 一次提交只做一件事；不要顺手格式化无关文件。

## 发布（重要）

**除非用户明确要求"发版 / 发布 / 打个 tag"，否则不要发 release。** 具体来说：

- 不要 `git tag`、不要 `git push origin v*`，也不要跑 `gh release create`；
- 打 tag 会自动触发 `.github/workflows/release.yml`，把二进制**公开**发布到 Releases，
  还会改 `latest` 指向——这是对外可见的动作，不能顺手做；
- 用户说"改完提交""推到 main"只包含提交与推送，**不包含发版**；
- 用户明确要发版时，也不要替用户写发布说明后直接发布：先给草稿，或按用户指定的版本号来。

日常开发只做到：改代码 → 本地构建 → `dotnet test tests/Unit` → 提交 → push main。

## 不要做的事

- 不要引入 GPL/AGPL 代码（本项目是 MIT）。FlClash 只作交互参考，不复制它的代码。
- 不要把 `~/.dsh/.credentials.yaml` 里的 secret 写进日志、配置文件或提交。
- 不要"顺手"改 dsh 本身的行为；本项目只是客户端。
- **不要未经要求打 tag / 发 release**（见上一节）。

## 已知限制

- 多标签页每个都是一个 WebView2 实例，内存占用随标签数线性增长；
- `dsh web` 的外部实例无法获取 launch token（只存在进程内存），登录靠 `LoopbackAuthServer` 的中转 cookie；
- CI 只做构建与打包，不跑需要 dsh 的端到端测试。
