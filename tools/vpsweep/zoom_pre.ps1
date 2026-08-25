# zoom_pre.ps1 - common preamble for every zoomed-sweep step.
# Dot-source it. Sets $Z, raises Concepts, asserts fullscreen, arms the guard.
#
# The idle gate is a START-OF-CALL gate only (see vp_lib FIX 4): our own
# SendInput holds idle near zero once we begin.

$ErrorActionPreference = "Stop"
$S = "C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad"
. "$S\q.ps1"; . "$S\vp_lib.ps1"; . "$S\vp_ui.ps1"
$script:Z = "$S\vpzoom"
if (-not (Test-Path $script:Z)) { New-Item -ItemType Directory -Path $script:Z -Force | Out-Null }

function ZPre([int]$MinIdle = 30) {
  $i = [Q]::Idle()
  if ($i -lt $MinIdle) { throw "STAND DOWN: idle only ${i}s - the user is at the machine" }
  $h = VP-Window
  if (-not [WV]::Alive($h)) { throw "ABORT: the Concepts window handle is dead" }
  VP-Raise $h | Out-Null
  Start-Sleep -Milliseconds 900
  $t = [Q]::FgTitle()
  if ($t -notlike "*Concepts*") { throw "ABORT: Concepts would not come forward (fg='$t')" }
  VP-AssertFullscreen $h | Out-Null
  $script:LastPut = $null          # re-arm; we have not put the cursor yet
  return "pre OK: idle=${i}s rect=$([WV]::Rect($h) -join ',') fg='$t'"
}

# Save the whole 2880x1800 frame plus the zoom/tilt readout beside it, so a
# capture is never ambiguous about which viewport it was taken in.
function ZShot([string]$name) {
  [Q]::Shot("$script:Z\$name.png")
  [Q]::ShotRegion(2140, 20, 760, 70, "$script:Z\$name -readout.png")
}
