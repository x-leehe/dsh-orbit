# 量取挂件窗口/客户区尺寸与位置（调试用）
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DshMeasure {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int L, T, R, B; }
  public static string Find(uint target) {
    string result = "not found";
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) {
        var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
        if (t.ToString() == "DSH 状态挂件") {
          RECT w, c; GetWindowRect(h, out w); GetClientRect(h, out c);
          result = "window=" + w.L + "," + w.T + " " + (w.R-w.L) + "x" + (w.B-w.T) +
                   "  client=" + c.R + "x" + c.B + "  visible=" + IsWindowVisible(h);
        }
      }
      return true; }, IntPtr.Zero);
    return result;
  }
}
"@
$p = Get-Process DshStatusWidget -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p) { Write-Host "pid=$($p.Id)  " ; Write-Host ([DshMeasure]::Find([uint32]$p.Id)) } else { Write-Host 'not running' }
