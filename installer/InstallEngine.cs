using System.Diagnostics;
using System.Reflection;

namespace DshOrbit.Setup;

/// <summary>
/// 安装引擎：预检 → 把嵌入的 payload 解包成 &lt;dir&gt;\DSH-Orbit.exe（先 .new 再原子替换）
/// → 便携标记 / 快捷方式 / 注册表。所有错误都以 <see cref="SetupException"/> 抛出，
/// 消息可以直接展示给用户；真正的失败会尽最大努力回滚，避免留下半装状态。
/// </summary>
internal sealed class InstallEngine
{
    private readonly InstallOptions _options;
    private readonly Action<InstallProgress>? _report;
    private readonly Func<string, bool>? _confirmCloseRunningApp;

    private long _payloadSize;
    private string _version = AppInfo.FallbackVersion;
    private readonly List<string> _shortcuts = new();
    private readonly List<string> _warnings = new();

    private bool _payloadExtracted;
    private bool _exeInstalled;
    private bool _exeExistedBefore;
    private bool _registryWritten;
    private bool _portableMarkerWritten;

    public InstallEngine(
        InstallOptions options,
        Action<InstallProgress>? report = null,
        Func<string, bool>? confirmCloseRunningApp = null)
    {
        _options = options;
        _report = report;
        _confirmCloseRunningApp = confirmCloseRunningApp;
    }

    public InstallResult Run()
    {
        try
        {
            Report(0, "正在准备安装…");

            using var payload = OpenPayload();
            _payloadSize = payload.Length;
            SetupLog.Info($"payload 大小：{_payloadSize / (1024.0 * 1024.0):F1} MB；目标目录：{_options.TargetDir}；模式：{_options.Mode}");

            Preflight();
            ExtractPayload(payload);
            ReplaceExecutable();

            if (_options.Portable)
            {
                Report(94, "正在写入便携模式标记…");
                ShellIntegration.WritePortableMarker(_options);
                _portableMarkerWritten = true;
            }
            else
            {
                CreateShortcuts();

                Report(95, "正在写入注册表…");
                ShellIntegration.WriteRegistry(_options, _version, _payloadSize);
                _registryWritten = true;
            }

            Report(100, "安装完成");
            SetupLog.Info($"安装完成：{_options.ExePath}（版本 {_version}）");

            var result = new InstallResult
            {
                Directory = _options.TargetDir,
                ExePath = _options.ExePath,
                Version = _version,
                Portable = _options.Portable,
            };
            result.Shortcuts.AddRange(_shortcuts);
            result.Warnings.AddRange(_warnings);
            return result;
        }
        catch (SetupException)
        {
            Rollback();
            throw;
        }
        catch (Exception ex)
        {
            Rollback();
            throw new SetupException($"安装没有完成：{ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------- 预检

    private void Preflight()
    {
        var dir = _options.TargetDir;

        SetupLog.Info($"预检：目录 {dir}");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            throw new SetupException(
                $"无法创建安装目录：\n{dir}\n\n{ex.Message}\n\n请换一个目录，或选择「仅为我安装」。", ex);
        }

        // 目录可写性：真写一个探测文件，比只看 ACL 靠谱。
        var probe = Path.Combine(dir, ".dsh-orbit-setup-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SetupException(
                $"没有权限写入安装目录：\n{dir}\n\n请选择「仅为我安装」（不需要管理员），或换一个有写权限的目录。", ex);
        }
        catch (Exception ex)
        {
            throw new SetupException($"安装目录不可写：\n{dir}\n\n{ex.Message}", ex);
        }

        // 已存在的旧 exe 如果也是只读，替换会失败，提前报出来。
        if (File.Exists(_options.ExePath) && (File.GetAttributes(_options.ExePath) & FileAttributes.ReadOnly) != 0)
        {
            SetupLog.Warn($"已存在的 {_options.ExePath} 是只读文件，将在替换前清除只读属性。");
        }

        CheckFreeSpace(dir);

        if (!_options.Portable)
        {
            ShellIntegration.ProbeRegistry(_options.Mode);

            if (Type.GetTypeFromProgID("WScript.Shell") is null)
            {
                _warnings.Add("系统里没有 WScript.Shell，快捷方式未能创建。");
                SetupLog.Warn("没有 WScript.Shell，将跳过快捷方式。");
            }
        }
    }

    private void CheckFreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }

            var required = _payloadSize + (8L * 1024 * 1024);
            SetupLog.Info($"磁盘可用空间 {drive.AvailableFreeSpace / (1024 * 1024)} MB，需要 {required / (1024 * 1024)} MB。");
            if (drive.AvailableFreeSpace < required)
            {
                throw new SetupException(
                    $"目标磁盘空间不足：还需要约 {required / (1024 * 1024)} MB 可用空间（当前约 {drive.AvailableFreeSpace / (1024 * 1024)} MB）。\n\n请清理磁盘，或换一个安装位置。");
            }
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"无法检查磁盘空间：{ex.Message}");
        }
    }

    private static Stream OpenPayload()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(AppInfo.PayloadResourceName);
        if (stream is null || stream.Length == 0)
        {
            stream?.Dispose();
            throw new SetupException(
                "安装包不完整：没有找到内置的主程序文件（payload.app）。\n\n请重新下载 DSH-Orbit-Setup.exe。");
        }

        return stream;
    }

    // ---------------------------------------------------------------- 解包

    private void ExtractPayload(Stream payload)
    {
        var temp = _options.TempExePath;
        Report(1, "正在复制文件…");

        try
        {
            long copied;
            byte[] sha256;

            // 注意：版本信息必须等文件句柄关闭后再读，否则 FileVersionInfo 拿不到独占的
            // 文件句柄会静默返回空版本（表现为 DisplayVersion 回退到 0.3.0）。
            using (var target = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                copied = 0;
                var lastPercent = -1;
                using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                    System.Security.Cryptography.HashAlgorithmName.SHA256);

                int read;
                while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    hasher.AppendData(buffer, 0, read);
                    copied += read;

                    // 复制阶段占进度条的 0–85%，剩下留给快捷方式/注册表。
                    var percent = (int)Math.Round(85.0 * copied / Math.Max(1L, _payloadSize));
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        Report(percent, "正在复制文件…");
                    }
                }

                target.Flush(flushToDisk: true);
                sha256 = hasher.GetHashAndReset();
            }

            if (copied != _payloadSize)
            {
                throw new SetupException(
                    $"安装包不完整：主程序资源只读出了 {copied} 字节，应为 {_payloadSize} 字节。\n\n请重新下载 DSH-Orbit-Setup.exe。");
            }

            _payloadExtracted = true;
            ReadPayloadVersion();
            SetupLog.Info(
                $"已写出 {temp}（{copied} 字节，sha256={Convert.ToHexString(sha256)}，版本 {_version}）。");
        }
        catch (Exception ex) when (ex is not SetupException)
        {
            TryDelete(temp);
            throw new SetupException($"复制主程序失败（可能是磁盘空间不足或目录被占用）：\n{temp}\n\n{ex.Message}", ex);
        }
    }

    private void ReadPayloadVersion()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(_options.TempExePath);
            var fileVersion = info.FileVersion?.Trim();
            var display = NormalizeDisplayVersion(fileVersion);
            if (!string.IsNullOrEmpty(display))
            {
                _version = display;
                SetupLog.Info($"payload 的 FileVersion={fileVersion}，DisplayVersion={display}。");
            }
            else
            {
                SetupLog.Warn($"payload 没有 FileVersion，DisplayVersion 回退到 {AppInfo.FallbackVersion}。");
            }
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"读取主程序版本失败，DisplayVersion 回退到 {AppInfo.FallbackVersion}：{ex.Message}");
        }
    }

    /// <summary>
    /// PE 的 FileVersion 常见是 4 段（0.3.0.0），「应用和功能」里显示 3 段更友好，
    /// 所以只在第 4 段为 0 时去掉它；其余情况原样使用 payload 的 FileVersion。
    /// </summary>
    private static string NormalizeDisplayVersion(string? fileVersion)
    {
        var value = (fileVersion ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var parts = value.Split('.');
        return parts.Length == 4 && parts[3] == "0"
            ? string.Join('.', parts, 0, 3)
            : value;
    }

    // ---------------------------------------------------------------- 替换

    private void ReplaceExecutable()
    {
        Report(86, "正在安装主程序…");

        var exe = _options.ExePath;
        _exeExistedBefore = File.Exists(exe);

        if (_exeExistedBefore)
        {
            if (InstallerSession.IsFileLocked(exe))
            {
                HandleRunningApp(exe);
            }

            try
            {
                File.SetAttributes(exe, FileAttributes.Normal);
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"清除旧 exe 只读属性失败：{ex.Message}");
            }
        }

        try
        {
            // 同卷 rename：要么旧文件还在，要么新文件已在位，不会出现半个 exe。
            File.Move(_options.TempExePath, exe, overwrite: true);
            SetupLog.Info($"主程序已安装：{exe}");
            _exeInstalled = true;
            _payloadExtracted = false;
        }
        catch (Exception ex)
        {
            TryDelete(_options.TempExePath);
            throw new SetupException($"无法写入主程序文件：\n{exe}\n\n{ex.Message}", ex);
        }
    }

    private void HandleRunningApp(string exe)
    {
        SetupLog.Info("目标 exe 正被占用，准备结束正在运行的 DSH-Orbit。");

        if (!InstallerSession.HasOrbitProcesses())
        {
            // 占用者不是 DSH-Orbit（杀毒扫描、别的程序打开着文件等），杀进程解决不了。
            throw new SetupException(
                $"目标文件被其他程序占用：\n{exe}\n\n请关闭正在使用它的程序后重试安装。");
        }

        if (!_options.Silent)
        {
            var confirmed = _confirmCloseRunningApp?.Invoke(
                "DSH-Orbit 正在运行，安装前需要先关闭它。\n\n是否立即关闭？");
            if (confirmed != true)
            {
                throw new SetupException("安装已取消：请先退出正在运行的 DSH-Orbit，然后重试。");
            }
        }

        InstallerSession.KillOrbitProcesses(exe);
        if (InstallerSession.IsFileLocked(exe))
        {
            // 路径读不到的实例（例如从别处启动的旧 exe）也一并清掉。
            InstallerSession.KillOrbitProcesses(expectedPath: null);
            for (var i = 0; i < 40 && InstallerSession.IsFileLocked(exe); i++)
            {
                Thread.Sleep(100);
            }
        }

        if (InstallerSession.IsFileLocked(exe))
        {
            throw new SetupException("无法关闭正在运行的 DSH-Orbit。\n\n请手动退出后重试安装。");
        }
    }

    // ---------------------------------------------------------------- 快捷方式

    private void CreateShortcuts()
    {
        if (!_options.DesktopShortcut && !_options.StartMenuShortcut)
        {
            Report(90, "正在收尾…");
            return;
        }

        Report(88, "正在创建快捷方式…");

        if (_options.DesktopShortcut)
        {
            TryCreateShortcut(ShellIntegration.DesktopShortcutPath(_options.Mode));
        }

        if (_options.StartMenuShortcut)
        {
            TryCreateShortcut(ShellIntegration.StartMenuShortcutPath(_options.Mode));
        }
    }

    private void TryCreateShortcut(string path)
    {
        try
        {
            ShellIntegration.CreateShortcut(path, _options.ExePath, _options.TargetDir);
            _shortcuts.Add(path);
        }
        catch (Exception ex)
        {
            // 快捷方式失败不让整个安装失败：主程序已经装好了，提示用户即可。
            _warnings.Add($"快捷方式创建失败：{path}");
            SetupLog.Warn($"快捷方式创建失败，继续安装：{path}；{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 回滚

    private void Rollback()
    {
        SetupLog.Warn("安装失败，开始回滚。");

        if (_payloadExtracted || File.Exists(_options.TempExePath))
        {
            TryDelete(_options.TempExePath);
        }

        foreach (var shortcut in _shortcuts)
        {
            ShellIntegration.DeleteShortcut(shortcut);
        }

        // 只有「全新安装」才删掉 exe；升级失败时保留原程序，别把用户原来的能用版本弄没。
        if (_exeInstalled && !_exeExistedBefore)
        {
            TryDelete(_options.ExePath);
        }

        if (_portableMarkerWritten)
        {
            TryDelete(_options.PortableMarkerPath);
            try
            {
                if (Directory.Exists(_options.DataDir) && Directory.GetFileSystemEntries(_options.DataDir).Length == 0)
                {
                    Directory.Delete(_options.DataDir);
                }
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"回滚 data 目录失败：{ex.Message}");
            }
        }

        if (_registryWritten)
        {
            ShellIntegration.RemoveRegistry(_options.Mode);
        }

        _shortcuts.Clear();
        _payloadExtracted = false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                SetupLog.Info($"已删除：{path}");
            }
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"删除文件失败（{path}）：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 进度

    private void Report(int percent, string status)
    {
        try
        {
            _report?.Invoke(new InstallProgress(Math.Clamp(percent, 0, 100), status));
        }
        catch (Exception ex)
        {
            // 进度回调失败（例如窗口已关闭）不影响安装本身。
            SetupLog.Warn($"进度回调失败：{ex.Message}");
        }
    }
}
