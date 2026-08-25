# zlib.ps1 - state-probed navigation of the Concepts settings panel.
#
# WHY THIS EXISTS. vp_sweep.ps1's VP-State probes sample x=1700-1706, which is
# where the panel sat on 2026-08-17. vp_ui.ps1 was re-measured on the 24th and
# puts the panel's left edge at ~2085 - so those probes read bare canvas and
# report "not in the editor" no matter what is on screen. Worse, the panel does
# NOT reliably reopen on the page it was closed from: closing it after one
# capture and reopening it for the next landed on the INTERACTION tab, where a
# blind click at the preset strip's coordinates lands on the Finger Action row.
# Nothing was changed that time - the click fell in dead text between the
# caption and the thumbnails - but that was luck, not design.
#
# So: probe, never assume. Every region below is calibrated against captures
# held on disk (probecal.py), not read off by eye.

$ErrorActionPreference = "Stop"
$S = "C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad"
. "$S\zoom_pre.ps1"

# --- panel geometry, re-measured for THIS run's panel position -------------
$script:TAB_WORK    = @(2279, 174)      # the "Workspace" tab
$script:BACKPILL    = @(2185, 530)      # "< Back" out of a grid editor
$script:EDITGRID2   = @(2736, 820)      # "Edit Grid" on the root page
$script:TYPE_Y2     = 1006              # grid-type thumbnail row
$script:TYPE_X2     = @{ '1-Point' = 2397; '2-Point' = 2561; '3-Point' = 2725
                         'No Grid' = 2397 }   # No Grid needs a rewind first
$script:STRIP_Y2    = 845               # preset row, editor at its top scroll
$script:STRIP_X02   = 2196
$script:STRIP_DX2   = 164
$script:STRIP_R6    = 2224              # entry 6 once the strip is at its right stop
$script:STRIP_WH    = @(2500, 845)
$script:SAFE_WHEEL  = @(2500, 400)      # page scroll: empty band under the tabs

function ZMean([int]$x,[int]$y,[int]$w,[int]$h) { [W]::RegionMean($x,$y,$w,$h) }

function ZProbe {
  $tw = ZMean 2180 206 200 12
  $ti = ZMean 2385 206 200 12
  $bp = ZMean 2130 508 115 46
  $tr = ZMean 2130 950 700 110
  $sr = ZMean 2130 790 620 110
  return @{
    tabWork  = [math]::Round($tw,1); tabInter = [math]::Round($ti,1)
    backPill = [math]::Round($bp,1); typeRow  = [math]::Round($tr,1)
    stripRow = [math]::Round($sr,1)
    onWork   = ($tw -gt 40); onInter = ($ti -gt 40)
    inEditor = ($bp -gt 150); atTypes = ($tr -gt 25)
  }
}

function ZDesc { $p = ZProbe; return ($p | ConvertTo-Json -Compress) }

# Bring the panel to the Workspace tab. Never guesses which tab is showing.
function ZWorkspace {
  VP-OpenPanel
  Start-Sleep -Milliseconds 600
  $p = ZProbe
  if ($p.onWork) { return $p }
  VP-Click $script:TAB_WORK[0] $script:TAB_WORK[1]
  Start-Sleep -Milliseconds 1200
  $p = ZProbe
  if (-not $p.onWork) { throw "ABORT: could not reach the Workspace tab ($(ZDesc))" }
  VP-Log "switched to Workspace tab"
  return $p
}

# Bring the Grid Type row on screen. Leaves any editor first, then scrolls only
# if it has to - and checks after every small step rather than wheeling blind.
function ZGotoTypes {
  $p = ZWorkspace
  if ($p.inEditor) {
    VP-Click $script:BACKPILL[0] $script:BACKPILL[1]
    Start-Sleep -Milliseconds 1600
    $p = ZProbe
  }
  for ($i = 0; $i -lt 14; $i++) {
    if ($p.atTypes) { break }
    [Q]::Wheel($script:SAFE_WHEEL[0], $script:SAFE_WHEEL[1], -2)
    $script:LastPut = @($script:SAFE_WHEEL[0], $script:SAFE_WHEEL[1])
    Start-Sleep -Milliseconds 320
    $p = ZProbe
  }
  if (-not $p.atTypes) { throw "ABORT: Grid Type row never came on screen ($(ZDesc))" }
  VP-Log "at Grid Type row ($(ZDesc))"
  return $p
}

# Select a perspective grid type and open its editor.
function ZEditor([string]$type) {
  $p = ZProbe
  if ($p.onWork -and $p.inEditor) {
    # already in AN editor - it may be the wrong type, so verify by title crop
    # and fall through to a full renavigation if asked for a different one
  }
  ZGotoTypes | Out-Null
  $x = $script:TYPE_X2[$type]
  if (-not $x) { throw "ABORT: no x for grid type '$type'" }
  # the type row sits at its right stop after a Back; rewind for the left half
  if ($type -eq 'No Grid') {
    for ($i = 0; $i -lt 14; $i++) { [Q]::Wheel(2500, $script:TYPE_Y2, 3); Start-Sleep -Milliseconds 120 }
    $script:LastPut = @(2500, $script:TYPE_Y2)
    Start-Sleep -Milliseconds 700
    $x = 2196                       # No Grid is entry 0 at the left stop
  } else {
    for ($i = 0; $i -lt 14; $i++) { [Q]::Wheel(2500, $script:TYPE_Y2, -3); Start-Sleep -Milliseconds 120 }
    $script:LastPut = @(2500, $script:TYPE_Y2)
    Start-Sleep -Milliseconds 700
  }
  [Q]::ShotRegion(2085, 920, 795, 180, "$script:Z\typerow-$($type -replace '[^A-Za-z0-9]','_').png")
  VP-Guard "before type click $type" | Out-Null
  VP-Log "grid type '$type' -> click $x,$($script:TYPE_Y2)"
  VP-Click $x $script:TYPE_Y2
  Start-Sleep -Milliseconds 2000
  if ($type -eq 'No Grid') { return }

  VP-Click $script:EDITGRID2[0] $script:EDITGRID2[1]
  Start-Sleep -Milliseconds 2200
  $p = ZProbe
  if (-not $p.inEditor) { throw "ABORT: Edit Grid did not open an editor ($(ZDesc))" }
  VP-Log "editor open for $type ($(ZDesc))"
}

# Click preset $idx of the open editor. Three stations, because only ~4.8 of
# the 164 px slots fit in a 795 px panel.
function ZPreset([int]$idx, [string]$name, [string]$tag) {
  $p = ZProbe
  if (-not $p.inEditor) { throw "ABORT: ZPreset called outside an editor ($(ZDesc))" }
  if ($idx -le 3)     { $station = "L"; $dir = 3;  $n = 18 }
  elseif ($idx -le 5) { $station = "M"; $dir = 3;  $n = 18 }
  else                { $station = "R"; $dir = -3; $n = 22 }
  for ($i = 0; $i -lt $n; $i++) { [Q]::Wheel($script:STRIP_WH[0], $script:STRIP_WH[1], $dir); Start-Sleep -Milliseconds 110 }
  $script:LastPut = @($script:STRIP_WH[0], $script:STRIP_WH[1])
  Start-Sleep -Milliseconds 700
  if ($station -eq "M") {
    for ($i = 0; $i -lt 3; $i++) { [Q]::Wheel($script:STRIP_WH[0], $script:STRIP_WH[1], -1); Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 600
  }
  switch ($station) {
    "L" { $x = $script:STRIP_X02 + $script:STRIP_DX2 * $idx }
    "M" { $x = $script:STRIP_X02 + $script:STRIP_DX2 * $idx - 222 }
    "R" { $x = $script:STRIP_R6  + $script:STRIP_DX2 * ($idx - 6) }
  }
  VP-Guard "before preset $name" | Out-Null
  VP-Log "preset[$idx] '$name' station=$station x=$x"
  VP-Click $x $script:STRIP_Y2
  Start-Sleep -Milliseconds 1500
  VP-Guard "after preset $name" | Out-Null
  [Q]::ShotRegion(2085, 760, 795, 180, "$script:Z\$tag-strip - $name.png")
  "$x" | Set-Content -Path "$script:Z\$tag-clickx - $name.txt" -Encoding ascii
  $p = ZProbe
  if (-not $p.inEditor) { throw "ABORT: the editor went away while selecting '$name' ($(ZDesc))" }
  return $x
}

function ZCapture([string]$name, [string]$tag) {
  VP-ClosePanel
  VP-Put 2050 1720
  Start-Sleep -Milliseconds 900
  VP-Guard "before shot $name" | Out-Null
  [Q]::Shot("$script:Z\$tag - $name.png")
  [Q]::ShotRegion(2140, 20, 760, 70, "$script:Z\$tag-zoom - $name.png")
  VP-Guard "after shot $name" | Out-Null
  VP-Log "captured '$tag - $name.png'"
}
