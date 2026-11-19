using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DshOrbit;

/// <summary>
/// 前台全屏探针：当前台窗口占满整个显示器时返回 true，挂件据此自动隐藏。
/// 只做判定——不返回句柄、不写日志、不缓存；检测失败一律当「不是全屏」。
/// 纯几何部分在 <see cref="CoversMonitor"/>，单测只测那一层。
/// </summary>
internal static class FullscreenProbe
{
    /// <summary>四边容差（px）：DWM 扩展边框、DPI 取整、Win10 隐形边框残留都落在 2px 内。</summary>
    private const int Tolerance = 2;

    /// <summary>类名黑名单：桌面与任务栏本身就铺满屏幕，不能算「前台全屏应用」。</summary>
    private static readonly string[] ShellClassNames =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    ];

    /// <summary>当前前台窗口是否占满整个显示器（含无边框全屏）。</summary>
    public static bool IsFullscreenForeground()
    {
        try
        {
            return IsFullscreenForegroundCore();
        }
        catch (EntryPointNotFoundException)
        {
            // 老系统缺 API：按「不是全屏」处理，绝不能让检测把心跳带崩。
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static bool IsFullscreenForegroundCore()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // 最小化 / 隐藏的窗口不可能是「正在看全屏」的前台。
        if (Native.IsIconic(hwnd) || !Native.IsWindowVisible(hwnd))
        {
            return false;
        }

        // 桌面窗口和任务栏铺满屏幕是常态，不算全屏应用。
        if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow())
        {
            return false;
        }

        if (HasShellClassName(hwnd))
        {
            return false;
        }

        // UWP 挂起、切到别的虚拟桌面时窗口还在，但已经被 DWM cloak 掉——屏幕上根本看不见它。
        if (IsCloaked(hwnd))
        {
            return false;
        }

        // 本进程自己的窗口（WebView2 外壳按 F11）也会盖满屏幕，但隐藏自己的挂件语义混乱。
        _ = Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        // 明确排除最大化窗口。取舍：任务栏设成「自动隐藏」时，普通最大化窗口正好盖满整个显示器，
        // 而那并不是用户想看全屏；真正的无边框全屏 / F11 / 游戏独占全屏都不是 maximize 状态，
        // 所以这条不会漏判。代价是「本身就最大化的外壳程序」被放过——宁可少藏，不要误藏。
        if (Native.IsZoomed(hwnd))
        {
            return false;
        }

        var window = WindowBounds(hwnd);
        var monitor = MonitorBounds(hwnd);
        return CoversMonitor(window, monitor);
    }

    /// <summary>
    /// 纯几何判定：窗口矩形是否完整覆盖显示器矩形（四边容差 2px）。
    /// 不碰任何 P/Invoke，可单测。
    /// </summary>
    internal static bool CoversMonitor(Rectangle window, Rectangle monitor)
    {
        // 显示器矩形无效（GetMonitorInfo 失败时我们给的就是空矩形）→ 无法判定，按不是全屏。
        if (monitor.Width <= 0 || monitor.Height <= 0)
        {
            return false;
        }

        // 空窗口（未显示 / 取矩形失败）不可能覆盖任何东西。
        if (window.Width <= 0 || window.Height <= 0)
        {
            return false;
        }

        return window.Left <= monitor.Left + Tolerance
            && window.Top <= monitor.Top + Tolerance
            && window.Right >= monitor.Right - Tolerance
            && window.Bottom >= monitor.Bottom - Tolerance;
    }

    /// <summary>
    /// 窗口矩形：优先 DWM 扩展边框（能去掉 Win10 左右各 7px 的隐形边框），
    /// 失败（返回非 0）或拿到空矩形时退回 GetWindowRect。
    /// </summary>
    private static Rectangle WindowBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttributeRect(
                hwnd,
                Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                Marshal.SizeOf<Native.RECT>()) == 0)
        {
            var bounds = ToRectangle(frame);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                return bounds;
            }
        }

        return Native.GetWindowRect(hwnd, out var legacy) ? ToRectangle(legacy) : Rectangle.Empty;
    }

    /// <summary>
    /// 窗口所在显示器（MONITOR_DEFAULTTONEAREST）的完整矩形。
    /// 用 rcMonitor 而不是 rcWork：全屏要连任务栏一起盖住。
    /// </summary>
    private static Rectangle MonitorBounds(IntPtr hwnd)
    {
        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return Rectangle.Empty;
        }

        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfoW(monitor, ref info) ? ToRectangle(info.rcMonitor) : Rectangle.Empty;
    }

    /// <summary>DWM cloaked 标记非 0 表示窗口当前没画在屏幕上（UWP 挂起 / 别的虚拟桌面）。</summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttributeInt(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
        && cloaked != 0;

    private static bool HasShellClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        if (Native.GetClassNameW(hwnd, buffer, buffer.Capacity) <= 0)
        {
            return false;
        }

        var name = buffer.ToString();
        foreach (var shell in ShellClassNames)
        {
            if (string.Equals(name, shell, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Rectangle ToRectangle(Native.RECT rect) =>
        Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
