# 更新日志

本文件是**发版的唯一来源**：`.github/workflows/release.yml` 会按 tag 从这里取正文，
取不到就直接失败——**没有 changelog 条目，发不了版**。

格式（严格）：

```
## [x.y.z] - YYYY-MM-DD
```

正文按 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 分组：`新增` / `变更` / `修复` / `移除`。
发版前把 `DshOrbit.csproj` 的 `<Version>` 改成同一条目的版本号，CI 会校验两者一致。

## [0.2.0] - 2026-11-15

一次大版本：**全部界面按 WinUI 3（Fluent）重塑，融合 Material 3**，并补上安装器、卸载器与单元测试。

### 新增

- **WinUI 3 × Material 3 设计系统**：新增 `Fluent.cs` 令牌层（颜色 / 版式阶梯 / 圆角 / 间距 / 海拔 / 状态层 / 缓动）与绘制原语，规范见 `docs/design-fluent.md`
- **浅色 / 深色双主题**：跟随系统，「外观」里可强制；强调色取系统强调色，Windows 设置里改颜色整个程序跟着变
- **安装器** `DSH-Orbit-Setup.exe`：自研的 7-Zip 风格单窗口，可选安装位置、三档范围（当前用户 / 所有用户 / 便携）、桌面与开始菜单快捷方式；写入「应用和功能」与 `RegisteredApplications`；支持 `/S`、`/D=`、`/MODE=` 静默部署
- **卸载**：`DSH-Orbit.exe --uninstall`，删除快捷方式、注册表与程序文件，默认保留配置与历史
- **便携模式**：exe 旁有 `portable.txt` 或 `data\` 时，配置与日志写进 `data\`，不碰 `%LOCALAPPDATA%`
- **地址栏收起**：标题栏右上角 `⌃ / ⌄` 一键收起工具条，网页区多出 36px，状态记在 `shell.json`
- **单元测试** `tests/Unit`：41 个方法 / 91 个用例（SVG 路径解析、颜色缓动、闲时时段、登录中转链接、菜单状态），已接入 CI
- **离屏渲染器**：一键产出「每个界面 × 浅色/深色」截图，用于设计自查
- 版本号写进程序集，`DisplayVersion` 从 payload 的 FileVersion 读取
- 托盘菜单支持 `↑↓` / `Enter` / `Esc` 键盘操作

### 变更

- 桌面卡片：30px 圆角 + 辉光 + 84px 圆形按钮 → 8px 圆角 WinUI 卡片 + 按钮组，端口号作为 `Title 28` 主视觉
- 托盘菜单：大图标磁贴 → `MenuFlyout` 结构（分组标题 + 分段控件 + 36px 行高）
- 浏览器外壳：双行头 → `TabView` 式标题栏（激活标签 2px 强调条）；刷新 / 在浏览器打开移入地址框内部
- 新标签页：深色渐变 → Mica 渐变 + Fluent 搜索框 + 2×2 主卡片 + 16px 圆角磁贴；历史记录上限 7 → 12
- 添加引擎对话框改为 `ContentDialog` 形态
- 字体统一为 `Segoe UI Variable` 版式阶梯，全程序最小字号 12px
- 地址栏折叠状态与主题选择都会持久化

### 修复

- 远程桌面把 DPI 切到 125% 后，外壳不会重新排版（现在 `OnDpiChanged` 会重跑布局）
- 托盘菜单「停止」态第一格不可点、「外壳关闭时」的状态值被裁切
- 浅色主题下外壳内容区仍是深色（WebView2 `PreferredColorScheme` 跟随主题）
- 卸载时误删用户数据的风险：默认改为保留，并加路径护栏

### 移除

- 卡片上的辉光、渐变、巨型圆形按钮
- 对 `HarmonyOS Sans SC` 的依赖（改用系统字体，中文由系统字体链回落）

## [0.1.1] - 2026-11-13

### 修复

- 托盘菜单在「已停止」状态下第一格没有可点的启动动作，现在会变成「启动」/「启动中」

## [0.1.0] - 2026-11-13

首个可用版本：桌面挂件（端口 / 插件 / 余额 / 今日已用）、托盘菜单、WebView2 外壳与多标签、本地登录中转。

[0.2.0]: https://github.com/x-leehe/dsh-orbit/releases/tag/v0.2.0
[0.1.1]: https://github.com/x-leehe/dsh-orbit/releases/tag/v0.1.1
[0.1.0]: https://github.com/x-leehe/dsh-orbit/releases/tag/v0.1.0
