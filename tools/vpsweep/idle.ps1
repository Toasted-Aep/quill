param(
  [int]$NeedSec = 90,
  [int]$MaxWaitSec = 1500,
  [int]$PollSec = 20,
  [switch]$Once
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Idle {
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  [DllImport("kernel32.dll")] public static extern uint GetTickCount();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

  public static double Seconds() {
    LASTINPUTINFO li = new LASTINPUTINFO();
    li.cbSize = (uint)Marshal.SizeOf(li);
    GetLastInputInfo(ref li);
    return (GetTickCount() - li.dwTime) / 1000.0;
  }
  public static string Fg() {
    IntPtr h = GetForegroundWindow();
    var sb = new System.Text.StringBuilder(512);
    GetWindowText(h, sb, 512);
    return h.ToString() + "|" + sb.ToString();
  }
  public static string Cursor() {
    POINT p; GetCursorPos(out p);
    return p.X + "," + p.Y;
  }
}
"@

if ($Once) {
  "idle={0:N1} fg={1} cursor={2}" -f [Idle]::Seconds(), [Idle]::Fg(), [Idle]::Cursor()
  exit 0
}

$deadline = (Get-Date).AddSeconds($MaxWaitSec)
while ((Get-Date) -lt $deadline) {
  $s = [Idle]::Seconds()
  "{0:HH:mm:ss} idle={1:N1}s fg={2}" -f (Get-Date), $s, [Idle]::Fg()
  if ($s -ge $NeedSec) { "IDLE-OK after {0:N1}s idle" -f $s; exit 0 }
  Start-Sleep -Seconds $PollSec
}
"IDLE-TIMEOUT"
exit 1
