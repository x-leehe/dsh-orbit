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
dotnet run --project tests/Render -c Release -- docs
```

涉及图标的改动必须重新跑 `tools/IconGen`，否则 `app.ico` 会过期。

## 代码约定

- 命名空间统一 `DshOrbit`；工程文件 `DshOrbit.csproj`，产物名 `DSH-Orbit`。
- 中文注释、中文 UI 文案；标识符用英文。
- 自绘 UI 统一走 GDI+（`Graphics` + `TextRenderingHint.AntiAlias`）；
  不要用 `TextRenderer`/GDI，它的文字画到带 alpha 的位图上会出现锯齿。
- 分层窗口（挂件）改动后要验证：透明、抗锯齿、点击穿透仍然正常。
- 配色：界面强调色是 `#7AAAFF`；挂件里"运行中"用绿色 `#3FD08A`（状态语义，不是品牌色）。
- 图标：只能用 Lucide 官方几何（`tools/fetch-lucide.ps1` → `LucideData.cs`），
  禁止手绘近似 Lucide 图标；品牌 logo（DSH 鲸鱼）来自 `DshMark.cs`。
- 不要把 `bin/`、`obj/`、`dist/`、`dist-lite/` 提交进仓库。

## 修改 WebView2 外壳时的注意点

- 窗口是"原生边框 + `WM_NCCALCSIZE` 去掉标题栏"的结构：
  改窗口样式可能破坏拖动、缩放、贴边吸附，改完要实测。
- 布局必须用 `GetClientRect` 的真实客户区，不要依赖 WinForms 缓存的 `ClientSize`。
- 自绘面板需要 `ResizeRedraw` + 双缓冲，否则缩放时会留下残影。

## 提交

- 提交信息用中文，`type: 简述`（`feat` / `fix` / `refactor` / `docs` / `chore`）。
- 一次提交只做一件事；不要顺手格式化无关文件。

## 不要做的事

- 不要引入 GPL/AGPL 代码（本项目是 MIT）。FlClash 只作交互参考，不复制它的代码。
- 不要把 `~/.dsh/.credentials.yaml` 里的 secret 写进日志、配置文件或提交。
- 不要"顺手"改 dsh 本身的行为；本项目只是客户端。

## 已知限制

- 多标签页每个都是一个 WebView2 实例，内存占用随标签数线性增长；
- `dsh web` 的外部实例无法获取 launch token（只存在进程内存），登录靠 `LoopbackAuthServer` 的中转 cookie；
- CI 只做构建与打包，不跑需要 dsh 的端到端测试。
