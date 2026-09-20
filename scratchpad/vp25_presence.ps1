# Run 25 presence gate.
#
# Run 24's correction (49.7 item 3): "static cursor + resetting idle" is ALSO a
# person who has just unlocked and is sitting still. CURSOR DISPLACEMENT is the
# trustworthy signal, so this reports moves and distance, not just uniqueness.
#
# LogonUI is checked SEPARATELY from OpenInputDesktop, because OpenInputDesktop
# has reported the session fine through five locks.
param([int]$Samples = 300, [int]$IntervalMs = 25)

if (-not ("P25" -as [type])) {
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public class P25{
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint f,bool inh,uint acc);
 [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
 [StructLayout(LayoutKind.Sequential)] public struct POINT{public int X;public int Y;}
 [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO{public uint cbSize;public uint dwTime;}
}
'@
}

$xs = New-Object System.Collections.Generic.List[int]
$ys = New-Object System.Collections.Generic.List[int]
$idles = New-Object System.Collections.Generic.List[double]
# $prevIdle starts as NaN, not MaxValue: seeding it high made sample 0 compare
# against MaxValue and count as a reset EVERY time, so every probe reported
# resets=1 and the number meant nothing. Exactly the shape of 49.7 item 2 - a
# driver that reports something it did not observe.
$moves = 0; $resets = 0; $maxJump = 0.0; $prevIdle = [double]::NaN

for ($i = 0; $i -lt $Samples; $i++) {
  $p = New-Object P25+POINT
  [void][P25]::GetCursorPos([ref]$p)
  if ($i -gt 0) {
    $dx = $p.X - $xs[$i-1]; $dy = $p.Y - $ys[$i-1]
    $d = [math]::Sqrt($dx*$dx + $dy*$dy)
    if ($d -gt 0) { $moves++ }
    if ($d -gt $maxJump) { $maxJump = $d }
  }
  $xs.Add($p.X); $ys.Add($p.Y)

  $li = New-Object P25+LASTINPUTINFO; $li.cbSize = 8
  [void][P25]::GetLastInputInfo([ref]$li)
  $idle = ([Environment]::TickCount - $li.dwTime) / 1000.0
  if (-not [double]::IsNaN($prevIdle) -and $idle -lt $prevIdle - 0.20) { $resets++ }
  $prevIdle = $idle
  $idles.Add($idle)
  Start-Sleep -Milliseconds $IntervalMs
}

$logon = @(Get-Process -Name LogonUI -ErrorAction SilentlyContinue).Count
$d = [P25]::OpenInputDesktop(0,$false,0x0001)
$deskOk = $d -ne [IntPtr]::Zero
if ($deskOk) { [void][P25]::CloseDesktop($d) }

$uniq = ($xs | ForEach-Object { $_ }) | Select-Object -Unique
$span = 0.0
for ($i = 1; $i -lt $xs.Count; $i++) {
  $dx = $xs[$i] - $xs[0]; $dy = $ys[$i] - $ys[0]
  $s = [math]::Sqrt($dx*$dx + $dy*$dy)
  if ($s -gt $span) { $span = $s }
}

# CURSOR DISPLACEMENT is the verdict. Idle resets alone are the signature a
# person sitting still at a keyboard produces, and 49.7 records that mistaking
# it for a phantom would have driven the mouse out from under somebody.
$clear = ($logon -eq 0) -and $deskOk -and ($moves -eq 0) -and ($span -eq 0) -and ($resets -eq 0)

"samples      : $Samples @ ${IntervalMs}ms"
"cursor       : $($xs[0]),$($ys[0]) -> $($xs[$xs.Count-1]),$($ys[$ys.Count-1])"
"moves        : $moves   maxjump=$([math]::Round($maxJump,1))px   displacement=$([math]::Round($span,1))px"
"idle         : $([math]::Round($idles[0],1))s -> $([math]::Round($idles[$idles.Count-1],1))s   resets=$resets"
"LogonUI      : $logon   (separate signal - OpenInputDesktop has lied five times)"
"InputDesktop : $deskOk"
"VERDICT      : " + $(if ($clear) { "CLEAR - no displacement, no resets, not locked" } else { "BLOCKED" })
if (-not $clear) { exit 1 }
