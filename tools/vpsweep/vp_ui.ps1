# vp_ui.ps1 - Concepts UI coordinates and navigation, fullscreen 2880x1800.
# Dot-source after q.ps1 and vp_lib.ps1.
#
# Every coordinate here was read off an earlier capture and is re-verified by
# the probe before the sweep runs - none of it is assumed.

# --- chrome ---------------------------------------------------------------
$script:GEAR      = @(2734, 62)    # top bar gear: opens the settings panel
$script:PANEL_X   = @(1722, 172)   # the panel's own close X
$script:PILL      = @(2200, 132, 112, 20)   # drag pill: bright iff panel open
$script:EDITGRID  = @(2734, 244)   # "Edit Grid" link on the Grid Type page
$script:BACK      = @(1774, 530)   # "< Back" inside the grid editor

# --- Grid Type row (panel scrolled to top) --------------------------------
$script:TYPE_Y    = 392
$script:TYPE_X0   = 1786           # centre of the first type thumbnail
$script:TYPE_DX   = 164
$script:TYPE_WHEEL= @(2400, 392)
$script:TYPES     = @('No Grid','Dot Grid','Graph Paper','Lined Paper','Isometric Grid','Triangle','1-Point','2-Point','3-Point')

# --- Preset strip (inside the grid editor) --------------------------------
$script:STRIP_Y     = 845          # centre of a preset thumbnail
$script:STRIP_X0    = 1788
$script:STRIP_DX    = 164
$script:STRIP_WHEEL = @(2400, 860)
$script:STRIP_REGION= @(1700, 740, 1180, 300)

# --- the parking spot every capture uses ----------------------------------
# On canvas, in a corner, far from every control. It is identical in the
# baseline frame so image differencing cancels the cursor entirely.
$script:PARK = @(2840, 1770)

function VP-PanelOpen {
  $m = [W]::RegionMean($script:PILL[0], $script:PILL[1], $script:PILL[2], $script:PILL[3])
  return @{ mean = [math]::Round($m,1); open = ($m -gt 15) }
}

function VP-OpenPanel {
  $s = VP-PanelOpen
  if ($s.open) { VP-Log "panel already open (pill=$($s.mean))"; return }
  VP-Click $script:GEAR[0] $script:GEAR[1]
  Start-Sleep -Milliseconds 1300
  $s = VP-PanelOpen
  VP-Log "opened panel, pill=$($s.mean) open=$($s.open)"
  if (-not $s.open) { throw "ABORT: clicking the gear did not open the panel (pill=$($s.mean))" }
}

function VP-ClosePanel {
  $s = VP-PanelOpen
  if (-not $s.open) { VP-Log "panel already closed (pill=$($s.mean))"; return }
  VP-Click $script:PANEL_X[0] $script:PANEL_X[1]
  Start-Sleep -Milliseconds 1000
  $s = VP-PanelOpen
  VP-Log "closed panel, pill=$($s.mean) open=$($s.open)"
  if ($s.open) { throw "ABORT: clicking X did not close the panel (pill=$($s.mean))" }
}

# Wheel a horizontal strip to one end. Concepts scrolls these strips
# horizontally from a VERTICAL wheel, 116 px per click (measured, see 15.5).
function VP-StripEnd([int[]]$at, [int]$dir, [int]$clicks = 18) {
  for ($i = 0; $i -lt $clicks; $i++) {
    [Q]::Wheel($at[0], $at[1], $dir)
    Start-Sleep -Milliseconds 130
  }
  $script:LastPut = @($at[0], $at[1])
  Start-Sleep -Milliseconds 500
}

# A clean full-frame capture: close the panel, park the cursor, shoot, reopen.
function VP-CaptureClean([string]$name) {
  VP-ClosePanel
  VP-Put $script:PARK[0] $script:PARK[1]
  Start-Sleep -Milliseconds 700
  VP-Guard "before shot $name"
  [Q]::Shot("$script:VPOUT\$name.png")
  VP-Log "captured $name.png"
  VP-Guard "after shot $name"
}
