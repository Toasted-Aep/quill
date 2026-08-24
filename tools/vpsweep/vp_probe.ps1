# vp_probe.ps1 - setup verification only. Changes NOTHING about the drawing.
# Establishes, rather than assumes:
#   * that Concepts is running, fullscreen, and on a scratch drawing
#   * the window rect that every fraction will be divided by
#   * whether the top bar overlays the canvas or shrinks it
# Aborts instantly if the machine is not idle or the user takes it back.

$ErrorActionPreference = "Stop"
$S = "C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad"
. "$S\q.ps1"
. "$S\vp_lib.ps1"
$O = $script:VPOUT

# ---- Rule zero gate. Re-checked here even though vp_wait.ps1 already passed.
$idle = [Q]::Idle()
if ($idle -lt 170) { throw "ABORT: machine idle is only ${idle}s - the user is at the keyboard" }
VP-Log "probe start, idle=${idle}s"

$p = VP-Concepts
$wins = [W]::ByPid([uint32]$p.Id)
$main = $null
foreach ($h in $wins) {
  $r = VP-Rect $h
  VP-Log ("window {0} '{1}' {2},{3} {4}x{5}" -f $h, [W]::Title($h), $r.L, $r.T, $r.W, $r.H)
  if ($r.W -gt 1000 -and $r.H -gt 800) { $main = $h }
}
if (-not $main) { throw "ABORT: no main Concepts window found" }
$rect = VP-Rect $main
VP-Log ("MAIN hwnd={0} rect={1},{2} -> {3},{4}  size={5}x{6}" -f $main,$rect.L,$rect.T,$rect.R,$rect.B,$rect.W,$rect.H)
"$($rect.L),$($rect.T),$($rect.R),$($rect.B)" | Set-Content -Path "$O\window_rect.txt" -Encoding ascii

$fullscreen = ($rect.L -le 0 -and $rect.T -le 0 -and $rect.W -ge [Q]::SW -and $rect.H -ge [Q]::SH)
VP-Log "fullscreen = $fullscreen  (screen $([Q]::SW)x$([Q]::SH))"

# ---- Raise it. This is a focus change WE make, so the guard is armed after.
[W]::Raise($main) | Out-Null
Start-Sleep -Milliseconds 900
$fg = [Q]::FgTitle()
VP-Log "after raise, foreground = '$fg'"
if ($fg -notlike "*Concepts*") { throw "ABORT: could not bring Concepts forward, fg='$fg'" }

# Park the cursor somewhere harmless and remember it, so the guard has a datum.
# Bottom-right of the canvas, away from every control and off the dial.
VP-Put 1500 1700
Start-Sleep -Milliseconds 400
VP-Guard "parked"

[Q]::Shot("$O\P0-initial.png")
VP-Log "captured P0-initial.png"
VP-Guard "after shot"

VP-Log "probe OK"
