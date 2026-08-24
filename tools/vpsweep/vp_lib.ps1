# vp_lib.ps1 - shared setup for the perspective-preset sweep.
# Dot-source AFTER q.ps1. Adds Concepts-specific guards and window helpers.

$script:VPS   = "C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad"
$script:VPOUT = "$script:VPS\vppresets"
if (-not (Test-Path $script:VPOUT)) { New-Item -ItemType Directory -Path $script:VPOUT -Force | Out-Null }

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);

  public static List<IntPtr> found = new List<IntPtr>();
  static uint wantPid;
  public static List<IntPtr> ByPid(uint pid) {
    found = new List<IntPtr>(); wantPid = pid;
    EnumWindows(new EnumProc(Cb), IntPtr.Zero);
    return found;
  }
  static bool Cb(IntPtr h, IntPtr p) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid == wantPid && IsWindowVisible(h)) found.Add(h);
    return true;
  }
  public static string Title(IntPtr h) { StringBuilder b = new StringBuilder(512); GetWindowTextW(h,b,512); return b.ToString(); }

  // Mean luminance of a screen region. Used to tell an open panel from a
  // closed one without guessing from a screenshot by eye.
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr h);
  public static double RegionMean(int x,int y,int w,int h) {
    using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format24bppRgb)) {
      using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(b))
        g.CopyFromScreen(x,y,0,0,new System.Drawing.Size(w,h), System.Drawing.CopyPixelOperation.SourceCopy);
      double s = 0;
      for (int j=0;j<h;j++) for (int i=0;i<w;i++) { System.Drawing.Color c = b.GetPixel(i,j); s += (c.R+c.G+c.B)/3.0; }
      return s/(w*h);
    }
  }
  // Foreground-stealing that actually works cross-thread.
  public static bool Raise(IntPtr h) {
    uint me = GetCurrentThreadId();
    uint pid;
    uint them = GetWindowThreadProcessId(h, out pid);
    AttachThreadInput(me, them, true);
    ShowWindow(h, 9); // SW_RESTORE
    bool ok = SetForegroundWindow(h);
    AttachThreadInput(me, them, false);
    return ok;
  }
}
"@ -ReferencedAssemblies System.Drawing

function VP-Concepts {
  $p = Get-Process -Name "TopHatch.Concepts" -ErrorAction SilentlyContinue
  if (-not $p) { throw "Concepts is not running" }
  return $p
}

function VP-Rect([IntPtr]$h) {
  $r = New-Object W+RECT
  [W]::GetWindowRect($h, [ref]$r) | Out-Null
  return @{ L=$r.L; T=$r.T; R=$r.R; B=$r.B; W=($r.R-$r.L); H=($r.B-$r.T) }
}

# The hard stop. Call before and after every injected step.
# Trips if the cursor is not where we last put it, or the foreground is not Concepts.
function VP-Guard([string]$tag, [int]$tol = 6) {
  $bad = @()
  if ($script:LastPut) {
    $dx = [Math]::Abs([Q]::Cx() - $script:LastPut[0])
    $dy = [Math]::Abs([Q]::Cy() - $script:LastPut[1])
    if ($dx -gt $tol -or $dy -gt $tol) {
      $bad += "cursor moved on its own: we left it at $($script:LastPut -join ',') and it is at $([Q]::Cursor())"
    }
  }
  $t = [Q]::FgTitle()
  if ($t -notlike "*Concepts*") { $bad += "foreground is '$t', not Concepts" }
  if ($bad.Count) { throw ("GUARD TRIPPED at [$tag]: " + ($bad -join " | ")) }
  return $true
}

function VP-Put([int]$x,[int]$y) { [Q]::Hover($x,$y); $script:LastPut = @($x,$y) }
function VP-Click([int]$x,[int]$y) { [Q]::Click($x,$y); $script:LastPut = @($x,$y) }
function VP-Log([string]$m) {
  $line = ("{0}  idle={1,7:N1}s  cur={2,-11} fg={3}" -f (Get-Date -Format "HH:mm:ss"), [Q]::Idle(), [Q]::Cursor(), [Q]::FgTitle()) + "  | $m"
  Write-Output $line
  Add-Content -Path "$script:VPOUT\run.log" -Value $line -Encoding utf8
}

# --------------------------------------------------------------------------
# FIX 2026-08-24: Concepts is a UWP app. Its top-level window is owned by
# ApplicationFrameHost.exe, NOT by TopHatch.Concepts.exe, so ByPid(concepts)
# returns an empty list and every caller concluded "no main window found".
# Find the frame window by title across all processes instead, and only fall
# back to the pid scan for a hypothetical non-UWP build.
# --------------------------------------------------------------------------
Add-Type -TypeDefinition @"
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public class WT {
  [DllImport("user32.dll")] public static extern bool EnumWindows(P cb, IntPtr p);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RC r);
  [StructLayout(LayoutKind.Sequential)] public struct RC { public int L,T,R,B; }
  public delegate bool P(IntPtr h,IntPtr p);
  static string want; public static List<IntPtr> hits = new List<IntPtr>();
  static bool Cb(IntPtr h,IntPtr p){
    if(!IsWindowVisible(h)) return true;
    StringBuilder b=new StringBuilder(512); GetWindowTextW(h,b,512);
    if(b.ToString().IndexOf(want,StringComparison.OrdinalIgnoreCase)<0) return true;
    RC r; GetWindowRect(h,out r);
    if((r.R-r.L)>1000 && (r.B-r.T)>800) hits.Add(h);
    return true; }
  public static List<IntPtr> Find(string title){ want=title; hits=new List<IntPtr>(); EnumWindows(new P(Cb),IntPtr.Zero); return hits; }
}
"@ -ReferencedAssemblies System.Drawing

function VP-MainWindow([string]$title = "Concepts") {
  $h = [WT]::Find($title)
  if ($h.Count -eq 1) { return $h[0] }
  if ($h.Count -gt 1) { throw "ABORT: $($h.Count) windows match '$title' - refusing to guess which is the app" }
  throw "ABORT: no visible top-level window titled '$title' (is Concepts minimised?)"
}

# --------------------------------------------------------------------------
# FIX 2026-08-24: [W]::Raise calls ShowWindow(h, SW_RESTORE) unconditionally,
# which UN-MAXIMISES an already-visible window. That silently changed the
# viewport out from under the sweep - fatal for a measurement whose whole
# premise is one frozen frame. Only restore a window that is actually
# minimised; otherwise just take the foreground.
# --------------------------------------------------------------------------
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;
public class WR {
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool f);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  public static bool SafeRaise(IntPtr h){
    uint pid; uint them = GetWindowThreadProcessId(h, out pid);
    uint me = GetCurrentThreadId();
    AttachThreadInput(me, them, true);
    if (IsIconic(h)) ShowWindow(h, 9);      // SW_RESTORE, minimised only
    bool ok = SetForegroundWindow(h);
    AttachThreadInput(me, them, false);
    return ok; }
}
"@
function VP-Raise([IntPtr]$h) { return [WR]::SafeRaise($h) }

# --------------------------------------------------------------------------
# FIX 2026-08-24 (3): going fullscreen reparents the Concepts frame under a
# fullscreen host, so EnumWindows stops returning it AND its title clears -
# both of the handles we were finding it by. The window is still perfectly
# valid; only the discovery path breaks. So: cache the handle once, and on
# every later call validate it directly (IsWindow + IsWindowVisible + rect)
# rather than rediscovering it.
# --------------------------------------------------------------------------
Add-Type -TypeDefinition @"
using System;using System.Text;using System.Runtime.InteropServices;
public class WV {
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RR r);
  [StructLayout(LayoutKind.Sequential)] public struct RR { public int L,T,R,B; }
  public static bool Alive(IntPtr h){ return IsWindow(h) && IsWindowVisible(h); }
  public static int[] Rect(IntPtr h){ RR r; GetWindowRect(h,out r); return new int[]{r.L,r.T,r.R-r.L,r.B-r.T}; }
  public static string Title(IntPtr h){ StringBuilder b=new StringBuilder(512); GetWindowTextW(h,b,512); return b.ToString(); }
}
"@

$script:HWNDFILE = "$script:VPS\vpsweep\concepts_hwnd.txt"

function VP-SetWindow([IntPtr]$h) {
  New-Item -ItemType Directory -Path (Split-Path $script:HWNDFILE) -Force | Out-Null
  [string]([int64]$h) | Set-Content -Path $script:HWNDFILE -Encoding ascii
}

function VP-Window {
  if (Test-Path $script:HWNDFILE) {
    $h = [IntPtr][int64](Get-Content $script:HWNDFILE -Raw).Trim()
    if ([WV]::Alive($h)) { return $h }
  }
  $h = VP-MainWindow "Concepts"      # pre-fullscreen discovery path
  VP-SetWindow $h
  return $h
}

# Fullscreen means EXACTLY the screen, at the origin. Anything else - windowed,
# maximised-with-borders (-13,-13 2906x1826 here) - is not fullscreen and must
# not be measured in, per the user's ruling.
function VP-AssertFullscreen([IntPtr]$h) {
  $r = [WV]::Rect($h)
  $ok = ($r[0] -eq 0 -and $r[1] -eq 0 -and $r[2] -eq [Q]::SW -and $r[3] -eq [Q]::SH)
  if (-not $ok) { throw "ABORT: Concepts is not fullscreen - rect=$($r[0]),$($r[1]) $($r[2])x$($r[3]), need 0,0 $([Q]::SW)x$([Q]::SH)" }
  return $true
}
