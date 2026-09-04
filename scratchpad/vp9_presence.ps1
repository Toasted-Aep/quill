# Presence gate, single process, DPI-aware, with the input-desktop check first.
# Reads only.  Injects nothing, presses nothing, moves nothing.
param([int]$Seconds = 60, [int]$IntervalMs = 40)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace VP9P -Name N -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr OpenInputDesktop(uint f, bool inherit, uint access);
[DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetUserObjectInformation(IntPtr h, int i, System.Text.StringBuilder p, int n, out uint len);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT p);
[DllImport("kernel32.dll")] public static extern uint GetTickCount();
[DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
public struct LASTINPUT { public uint cbSize; public uint dwTime; }
'@ -ReferencedAssemblies System.Drawing

try { [VP9P.N]::SetProcessDpiAwareness(2) | Out-Null } catch {}

$h = [VP9P.N]::OpenInputDesktop(0, $false, 0x0100)
if ($h -eq [IntPtr]::Zero) {
  "INPUT DESKTOP : <UNREACHABLE - another desktop has the input>"
} else {
  $sb = New-Object System.Text.StringBuilder 256; $n = 0
  [VP9P.N]::GetUserObjectInformation($h, 2, $sb, 256, [ref]$n) | Out-Null
  [VP9P.N]::CloseDesktop($h) | Out-Null
  "INPUT DESKTOP : $($sb.ToString())"
}
$logon = @(Get-Process LogonUI -ErrorAction SilentlyContinue)
"LogonUI       : $(if ($logon.Count) {'RUNNING - LOCKED'} else {'not running - unlocked'})"
"Quill         : $(@(Get-Process Quill -ErrorAction SilentlyContinue).Count) process(es)"

function Idle() { $li = New-Object 'VP9P.N+LASTINPUT'; $li.cbSize = 8; [VP9P.N]::GetLastInputInfo([ref]$li) | Out-Null; [math]::Round(([VP9P.N]::GetTickCount() - $li.dwTime)/1000.0, 2) }

$p = New-Object System.Drawing.Point
[VP9P.N]::GetCursorPos([ref]$p) | Out-Null
$prev = "$($p.X),$($p.Y)"; $start = $prev
$sw = [Diagnostics.Stopwatch]::StartNew()
$samples = 0; $moves = 0; $resets = 0; $lastIdle = Idle
$pos = @{}; $pos[$prev] = 1
$track = New-Object Collections.Generic.List[string]
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  [VP9P.N]::GetCursorPos([ref]$p) | Out-Null
  $cur = "$($p.X),$($p.Y)"
  $samples++
  if (-not $pos.ContainsKey($cur)) { $pos[$cur] = 0 }
  $pos[$cur]++
  if ($cur -ne $prev) {
    $moves++
    $a = $prev -split ','; $b = $cur -split ','
    $d = [math]::Round([math]::Sqrt([math]::Pow([int]$b[0]-[int]$a[0],2) + [math]::Pow([int]$b[1]-[int]$a[1],2)), 1)
    if ($track.Count -lt 60) { $track.Add(("t={0,7:N0}  {1} -> {2}  step={3}px" -f $sw.Elapsed.TotalMilliseconds, $prev, $cur, $d)) }
    $prev = $cur
  }
  $i = Idle
  if ($i -lt $lastIdle - 0.3) { $resets++ }
  $lastIdle = $i
  Start-Sleep -Milliseconds $IntervalMs
}
$fw = [VP9P.N]::GetForegroundWindow(); $t = New-Object Text.StringBuilder 256
[VP9P.N]::GetWindowText($fw, $t, 256) | Out-Null
""
"samples            : $samples over $([math]::Round($sw.Elapsed.TotalSeconds,1)) s at $IntervalMs ms"
"cursor start       : $start"
"cursor end         : $prev"
"distinct positions : $($pos.Keys.Count)"
"cursor changes     : $moves"
"idle resets        : $resets   (idle now $lastIdle s)"
"foreground window  : '$($t.ToString())'"
if ($track.Count) { ""; "TRACK:"; $track | ForEach-Object { $_ } }
