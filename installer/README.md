# DSH-Orbit 安装程序

自研的 Windows 安装器（**不用 Inno Setup / NSIS / WiX / MSI**）：一个固定大小的 WinForms 单窗口，
把主程序 `DSH-Orbit.exe` 作为嵌入资源（`payload.app`）打进安装器自身，运行后选路径 + 选项，直接装。

- 界面：7-Zip 那种「一张脸」——没有向导页；装完在同一个窗口切成完成态。
- 视觉：复用仓库的 `Fluent.cs` / `FluentControls.cs`（WinUI 3 令牌），logo 用 `OrbitMark`。
- 窗口：640×440 逻辑像素、不可缩放；无边框 + 自绘标题栏（logo / 版本号 / 最小化 / 关闭），
  标题栏和正文共用 Fluent 令牌，所以深浅色不会出现「浅色标题栏配深色内容」；
  同时仍给 DWM 设沉浸式深色与 Win11 圆角属性（`DwmSetWindowAttribute`）。
- 单文件：`SelfContained + PublishSingleFile + EnableCompressionInSingleFile`，产物只有一个 exe。

## 构建

```powershell
cd C:\Users\admin\Documents\MyProjects\dsh-orbit

# 1. 准备 payload（dist\DSH-Orbit.exe 由 build.ps1 产出）
dotnet build DshOrbit.csproj -c Release
.\build.ps1        # 或者只要 dotnet publish ... -o dist（见 build.ps1）

# 2. 发布安装器 -> installer\dist\DSH-Orbit-Setup.exe
dotnet publish installer\Setup.csproj -c Release -o installer\dist
```

payload 默认取 `..\dist\DSH-Orbit.exe`（自包含、约 47 MB），**不是** `dist-lite`。可以覆盖：

```powershell
dotnet publish installer\Setup.csproj -c Release -o installer\dist -p:PayloadPath=..\dist-lite\DSH-Orbit.exe
```

payload 缺失时构建仍然成功，但运行安装器会提示「安装包不完整」（静默模式返回 2）。

> 发布时如果别的进程正在重写 `dist\DSH-Orbit.exe`，可能把半个文件嵌进来。
> 追求可复现时可以先把 payload 复制到固定路径，再用 `-p:PayloadPath=<固定路径>` 发布；
> 安装日志里会记录解包出的文件大小与 SHA256，方便核对。

## 安装范围的默认目录

| 范围 | 默认目录 | 注册表 | 快捷方式 |
|---|---|---|---|
| 仅为我安装 | `%LOCALAPPDATA%\Programs\DSH-Orbit` | HKCU | 用户桌面 / 用户开始菜单 |
| 为所有用户安装 | `C:\Program Files\DSH-Orbit`（若 `C:\Program Files (x86)\DSH-Orbit` 已存在则用它） | HKLM | 公共桌面 / 公共开始菜单 |
| 便携模式 | `%USERPROFILE%\DSH-Orbit-Portable` | 不写 | 不建 |

切换范围时自动更新路径；用户手动改过路径后不再覆盖。

## 命令行（CI / 自动化）

```text
/S                      静默安装（无界面、无对话框；失败写日志并返回非 0）
/D=<dir>                安装目录，必须是最后一个参数（允许带空格，不需要引号）
/MODE=machine|user|portable
/NODESKTOP              不创建桌面快捷方式
/NOSTART                不创建开始菜单项
/LOG=<file>             额外日志文件（默认 %TEMP%\DSH-Orbit-Setup.log 始终写）
```

示例：

```powershell
# 静默装到临时目录，不要快捷方式
.\installer\dist\DSH-Orbit-Setup.exe /S /MODE=user /NODESKTOP /NOSTART /D=C:\Temp\orbit-test

# 带空格的路径：/D= 之后整段都算路径
.\installer\dist\DSH-Orbit-Setup.exe /S /MODE=portable /D=C:\Temp\orbit portable
```

静默模式退出码：`0` 成功，`1` 未预期错误，`2` 预检/参数错误，`3` 用户取消了 UAC。

## 提权

安装器清单是 `asInvoker`。只有选「为所有用户安装」时，点「安装」才会
`Process.Start(self, "--elevated <原参数>", Verb="runas")` 重启自己，然后本窗口退出；
提权后的实例直接进入安装流程。静默模式下会等待提权实例结束并透传退出码。

## 装了什么

非便携模式：

1. `<dir>\DSH-Orbit.exe`（先写 `.new`，再 `File.Move(overwrite: true)` 原子替换；目标被占用时先结束运行中的 DSH-Orbit，UI 模式会先问一句，静默模式直接结束进程）
2. 快捷方式（按勾选）：`DSH-Orbit.lnk` + `Programs\DSH-Orbit\DSH-Orbit.lnk`
3. 注册表：`Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-Orbit`、
   `Software\DSH-Orbit\Capabilities`、`Software\RegisteredApplications` 下的 `DSH-Orbit`

其中 `DisplayVersion` 取 payload 的 FileVersion；若是 `0.3.0.0` 这种末段为 0 的 4 段版本，会去掉末尾的 `.0`
（显示为 `0.3.0`），读不到时回退到 `0.3.0`。

便携模式只写 `<dir>\DSH-Orbit.exe`、`<dir>\data\`（空目录）和 `<dir>\portable.txt`
（主程序认 `portable.txt` 或 `data\` 任一信号，两者都建）。

## 失败与回滚

- 写盘前先做预检：payload 是否存在、目录是否可写、磁盘空间是否够、注册表是否可写；
  这几种失败不会留下任何安装痕迹（静默模式写日志并返回非 0）。
- 解包写 `.new` 失败会删临时文件；后续步骤失败会尽量回滚快捷方式/注册表，
  全新安装还会删掉已复制的 exe（升级失败则保留原程序）。

## 开发：界面截图

```powershell
.\installer\dist\DSH-Orbit-Setup.exe --shot artifacts\setup\idle-dark.png  --shot-state idle  --shot-theme dark
.\installer\dist\DSH-Orbit-Setup.exe --shot artifacts\setup\done-light.png --shot-state done  --shot-theme light
```

`--shot-state` 取值：`idle` / `progress` / `portable` / `machine` / `done` / `error`；
`--shot-theme` 取值：`system` / `light` / `dark`。

窗口是无边框 + 自绘标题栏，所以 `DrawToBitmap` 截出来的就是完整外观（含标题栏），
深浅色各自成立，不需要屏幕拷贝。

## 源码结构

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口：静默 / 图形 / 截图；退出码 |
| `CliOptions.cs` | 命令行解析、提权参数拼装 |
| `Model.cs` | `InstallMode`、`InstallOptions`（默认目录）、`InstallProgress/Result`、`SetupException` |
| `InstallEngine.cs` | 预检、payload 解包、原子替换、快捷方式、注册表、回滚 |
| `ShellIntegration.cs` | COM `WScript.Shell` 快捷方式、注册表写入/清理、便携标记 |
| `InstallerSession.cs` | 管理员判断、runas 提权、结束运行中的主程序、文件占用检测 |
| `SetupLog.cs` | 日志（默认 `%TEMP%\DSH-Orbit-Setup.log`，`/LOG=` 额外一份） |
| `SetupForm.cs` | 单窗口 Fluent 自绘界面（收音机/复选框/进度条为私有绘制） |
