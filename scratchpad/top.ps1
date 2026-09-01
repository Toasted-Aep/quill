# top.ps1 - keep Quill at the top of the z-order for the duration of a run.
#
# Concepts (the user's live handwriting app) is maximised on this machine and
# RE-ACTIVATES ITSELF after being minimised, so a foreground assert alone leaves
# a millisecond-wide window in which an injected click could land in the user's
# document. HWND_TOPMOST closes that window structurally: injected mouse input
# is hit-tested by z-order, and a topmost Quill is above every normal window
# whether or not it holds the foreground.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Top {
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int t, uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
  static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
  const uint NOMOVE = 0x0002, NOSIZE = 0x0001, NOACTIVATE = 0x0010;
  public static bool Pin(IntPtr h)   { return SetWindowPos(h, HWND_TOPMOST,   0,0,0,0, NOMOVE|NOSIZE|NOACTIVATE); }
  public static bool Unpin(IntPtr h) { return SetWindowPos(h, HWND_NOTOPMOST, 0,0,0,0, NOMOVE|NOSIZE|NOACTIVATE); }
  // Which process owns the window the cursor is actually over. This is the
  // question that matters before a click, and it is not the same question as
  // "what is the foreground window".
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  // Push every window whose title matches down to the bottom of the z-order and
  // minimise it. Used only on Concepts, which re-activates itself; the document
  // is never touched.
  public static int Demote(string title) {
    int n = 0;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var sb = new System.Text.StringBuilder(400);
      GetWindowTextW(h, sb, 400);
      if (sb.ToString() != title) return true;
      ShowWindow(h, 6);                                  // SW_MINIMIZE
      n++;
      return true;
    }, IntPtr.Zero);
    return n;
  }
  public static uint PidUnderCursor() {
    POINT p; GetCursorPos(out p);
    IntPtr h = WindowFromPoint(p);
    if (h == IntPtr.Zero) return 0;
    IntPtr root = GetAncestor(h, 2);            // GA_ROOT
    if (root == IntPtr.Zero) root = h;
    uint pid; GetWindowThreadProcessId(root, out pid); return pid;
  }
}
"@
