# fg.ps1 - reliable foreground switch. SetForegroundWindow alone is refused when
# the caller is not already the foreground process, which is exactly our case:
# the terminal keeps taking focus back between PowerShell calls. The
# AttachThreadInput dance is the documented way round it.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Fg {
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

  public static bool Force(IntPtr target) {
    if (target == IntPtr.Zero) return false;
    if (IsIconic(target)) ShowWindow(target, 9);      // SW_RESTORE
    IntPtr fore = GetForegroundWindow();
    if (fore == target) return true;
    uint pid;
    uint foreTid = GetWindowThreadProcessId(fore, out pid);
    uint thisTid = GetCurrentThreadId();
    bool attached = false;
    if (foreTid != thisTid) attached = AttachThreadInput(thisTid, foreTid, true);
    BringWindowToTop(target);
    SetForegroundWindow(target);
    if (attached) AttachThreadInput(thisTid, foreTid, false);
    return GetForegroundWindow() == target;
  }
}
"@
