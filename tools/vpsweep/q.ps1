# q.ps1 - dot-source this. SendInput helper + watchdog + capture.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;

public class Q {
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUT {
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public MOUSEINPUT mi;
    [FieldOffset(8)] public KEYBDINPUT ki;
  }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] p, int size);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] public static extern uint GetTickCount();
  [DllImport("Shcore.dll")] public static extern int SetProcessDpiAwareness(int v);

  const uint MOVE=0x0001, LDOWN=0x0002, LUP=0x0004, ABS=0x8000;
  const uint KEYUP=0x0002;

  public static int SW { get { return GetSystemMetrics(0); } }
  public static int SH { get { return GetSystemMetrics(1); } }

  static INPUT MoveInput(int x, int y) {
    INPUT i = new INPUT();
    i.type = 0;
    i.mi.dwFlags = MOVE | ABS;
    i.mi.dx = (int)(((double)x) * 65535.0 / (SW - 1));
    i.mi.dy = (int)(((double)y) * 65535.0 / (SH - 1));
    return i;
  }
  public static void MoveTo(int x, int y) {
    INPUT[] a = new INPUT[1]; a[0] = MoveInput(x,y);
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }
  // Double-tapped absolute injection: one MoveTo alone does not always produce a
  // hover here, two separated by a beat does.
  public static void Hover(int x, int y) { MoveTo(x,y); System.Threading.Thread.Sleep(110); MoveTo(x,y); }
  public static void Down() { INPUT[] a = new INPUT[1]; a[0].type=0; a[0].mi.dwFlags=LDOWN; SendInput(1,a,Marshal.SizeOf(typeof(INPUT))); }
  public static void Up()   { INPUT[] a = new INPUT[1]; a[0].type=0; a[0].mi.dwFlags=LUP;   SendInput(1,a,Marshal.SizeOf(typeof(INPUT))); }
  public static void Click(int x, int y) { Hover(x,y); System.Threading.Thread.Sleep(100); Down(); System.Threading.Thread.Sleep(70); Up(); }

  const uint WHEEL=0x0800, HWHEEL=0x1000;
  public static void Wheel(int x, int y, int clicks) {
    Hover(x,y); System.Threading.Thread.Sleep(80);
    INPUT[] a = new INPUT[1]; a[0].type=0; a[0].mi.dwFlags=WHEEL; a[0].mi.mouseData=(uint)(clicks*120);
    SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
  }
  public static void HWheel(int x, int y, int clicks) {
    Hover(x,y); System.Threading.Thread.Sleep(80);
    INPUT[] a = new INPUT[1]; a[0].type=0; a[0].mi.dwFlags=HWHEEL; a[0].mi.mouseData=(uint)(clicks*120);
    SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
  }

  public static void Key(ushort vk) {
    INPUT[] a = new INPUT[1]; a[0].type=1; a[0].ki.wVk=vk; SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(40);
    a[0].ki.dwFlags=KEYUP; SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
  }
  public static void KeyMod(ushort mod, ushort vk) {
    INPUT[] a = new INPUT[1];
    a[0].type=1; a[0].ki.wVk=mod; SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
    a[0].ki.wVk=vk;  a[0].ki.dwFlags=0;     SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(40);
    a[0].ki.wVk=vk;  a[0].ki.dwFlags=KEYUP; SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
    a[0].ki.wVk=mod; a[0].ki.dwFlags=KEYUP; SendInput(1,a,Marshal.SizeOf(typeof(INPUT)));
  }

  // press at (x0,y0), travel to (x1,y1) in n steps, release. Real WM_POINTER-ish
  // motion: every step is its own injected absolute move.
  public static void Drag(int x0,int y0,int x1,int y1,int steps,int stepMs) {
    Hover(x0,y0);
    System.Threading.Thread.Sleep(150);
    Down();
    System.Threading.Thread.Sleep(90);
    for (int i=1;i<=steps;i++) {
      double f = (double)i/steps;
      MoveTo((int)Math.Round(x0+(x1-x0)*f), (int)Math.Round(y0+(y1-y0)*f));
      System.Threading.Thread.Sleep(stepMs);
    }
    System.Threading.Thread.Sleep(120);
    Up();
  }
  // press, then follow an explicit polyline
  public static void DragPath(int[] xs, int[] ys, int stepMs) {
    Hover(xs[0], ys[0]);
    System.Threading.Thread.Sleep(150);
    Down();
    System.Threading.Thread.Sleep(90);
    for (int i=1;i<xs.Length;i++) { MoveTo(xs[i], ys[i]); System.Threading.Thread.Sleep(stepMs); }
    System.Threading.Thread.Sleep(120);
    Up();
  }

  public static double Idle() {
    LASTINPUTINFO l = new LASTINPUTINFO(); l.cbSize=(uint)Marshal.SizeOf(l);
    GetLastInputInfo(ref l);
    return (GetTickCount()-l.dwTime)/1000.0;
  }
  public static string Cursor() { POINT p; GetCursorPos(out p); return p.X+","+p.Y; }
  public static int Cx() { POINT p; GetCursorPos(out p); return p.X; }
  public static int Cy() { POINT p; GetCursorPos(out p); return p.Y; }
  public static string FgTitle() {
    IntPtr h = GetForegroundWindow();
    System.Text.StringBuilder b = new System.Text.StringBuilder(512);
    GetWindowTextW(h, b, 512);
    return b.ToString();
  }
  public static IntPtr Fg() { return GetForegroundWindow(); }
  public static uint FgPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }

  public static void Shot(string path) {
    Bitmap bmp = new Bitmap(SW, SH, PixelFormat.Format24bppRgb);
    using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(0,0,0,0,new Size(SW,SH), CopyPixelOperation.SourceCopy); }
    bmp.Save(path, ImageFormat.Png); bmp.Dispose();
  }
  public static void ShotRegion(int x,int y,int w,int h,string path) {
    Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(x,y,0,0,new Size(w,h), CopyPixelOperation.SourceCopy); }
    bmp.Save(path, ImageFormat.Png); bmp.Dispose();
  }
  // Burst: hold n region grabs in RAM with timestamps, save afterwards.
  public static List<Bitmap> burst = new List<Bitmap>();
  public static List<double> burstMs = new List<double>();
  public static void BurstClear() { foreach (Bitmap b in burst) b.Dispose(); burst.Clear(); burstMs.Clear(); }
  public static void BurstGrab(int x,int y,int w,int h,double ms) {
    Bitmap bmp = new Bitmap(w,h,PixelFormat.Format24bppRgb);
    using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(x,y,0,0,new Size(w,h), CopyPixelOperation.SourceCopy); }
    burst.Add(bmp); burstMs.Add(ms);
  }
  public static void BurstSave(string dir, string prefix) {
    for (int i=0;i<burst.Count;i++) burst[i].Save(dir+"\\"+prefix+i.ToString("00")+".png", ImageFormat.Png);
  }
  public static string Pixel(int x,int y) {
    Bitmap bmp = new Bitmap(1,1,PixelFormat.Format24bppRgb);
    using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(x,y,0,0,new Size(1,1), CopyPixelOperation.SourceCopy); }
    Color c = bmp.GetPixel(0,0); bmp.Dispose();
    return String.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
  }
}
"@ -ReferencedAssemblies System.Drawing

try { [Q]::SetProcessDpiAwareness(2) | Out-Null } catch {}

$script:LastPut = $null      # where WE last put the cursor

function Put([int]$x,[int]$y) { [Q]::Hover($x,$y); $script:LastPut = @($x,$y) }

# Watchdog: the user is back if the cursor is not where we left it, or the
# foreground window is not the one we are driving.
function Guard([string]$expectTitle = "Quill", [int]$tol = 6) {
  $bad = @()
  if ($script:LastPut) {
    $dx = [Math]::Abs([Q]::Cx() - $script:LastPut[0])
    $dy = [Math]::Abs([Q]::Cy() - $script:LastPut[1])
    if ($dx -gt $tol -or $dy -gt $tol) { $bad += "cursor moved: expected $($script:LastPut -join ',') got $([Q]::Cursor())" }
  }
  $t = [Q]::FgTitle()
  if ($expectTitle -and $t -notlike "*$expectTitle*") { $bad += "foreground is '$t', not *$expectTitle*" }
  if ($bad.Count) { throw ("GUARD TRIPPED: " + ($bad -join " | ")) }
  return $true
}

function Sw { [Q]::SW }
function Sh { [Q]::SH }
