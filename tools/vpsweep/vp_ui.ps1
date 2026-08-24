# vp_ui.ps1 - Concepts UI coordinates and navigation, fullscreen 2880x1800.
# Dot-source after q.ps1 and vp_lib.ps1.
#
# RE-MEASURED 2026-08-24. The previous values in this file were read off the
# 2026-08-17 run and are WRONG for the panel as it now sits: the settings panel
# is anchored to the right with a different inset, so its left edge is x=2127,
# not ~1700 (see 15.4e - a panel's position is an inset from the side it is
# anchored to, and the user can move it). Everything below was measured off a
# live frame in this run, by differencing a panel-open capture against a
# panel-closed one, and every click is verified by screenshot before the next.
#
# NOTHING HERE IS SAFE TO CARRY FORWARD BLIND. Re-derive it at the top of any
# future run; the cost is one screenshot.

# --- chrome ---------------------------------------------------------------
$script:GEAR      = @(2734, 62)     # top bar gear: opens the settings panel
$script:PANEL_L   = 2127            # measured left edge of the open panel
$script:PANEL_X   = @(2130, 174)    # the panel's own close X
$script:EDITGRID  = @(2734, 820)    # "Edit Grid" link on the Grid Type page

# Panel-open probe. The old one sampled a "drag pill" at (2200,132) that reads
# 2.5 open vs 1.7 closed - i.e. it never worked. The panel HEADER (the
# Workspace / Interaction tab row) persists on every page of the panel,
# including the grid editor, and reads 16.5 open vs 1.3 closed.
$script:HDR       = @(2140, 150, 700, 60)
$script:HDR_MIN   = 8.0

# --- Grid Type row (panel scrolled to top) --------------------------------
$script:TYPE_Y    = 1006
$script:TYPE_X0   = 2194            # centre of the first type thumbnail
$script:TYPE_DX   = 164
$script:TYPE_WHEEL= @(2500, 1006)
$script:TYPES     = @('No Grid','Dot Grid','Graph Paper','Lined Paper','Isometric Grid','Triangle','1-Point','2-Point','3-Point')

# --- Preset strip (inside the grid editor) --------------------------------
# Measured in this run; see VP-FindStrip, which re-locates the row rather than
# trusting these.
$script:STRIP_Y     = 1006
$script:STRIP_X0    = 2194
$script:STRIP_DX    = 164
$script:STRIP_WHEEL = @(2500, 1006)
$script:STRIP_REGION= @(2127, 900, 753, 260)

# --- the parking spot every capture uses ----------------------------------
# On canvas: right of the bottom mode bar (which ends at x=1920), left of the
# panel (x=2127), and far below the horizon so the pointer can never sit on a
# vanishing-point dot. Identical in the baseline, so differencing cancels it.
$script:PARK = @(2050, 1720)

function VP-PanelOpen {
  $m = [W]::RegionMean($script:HDR[0], $script:HDR[1], $script:HDR[2], $script:HDR[3])
  return @{ mean = [math]::Round($m,1); open = ($m -gt $script:HDR_MIN) }
}

function VP-OpenPanel {
  $s = VP-PanelOpen
  if ($s.open) { return }
  VP-Click $script:GEAR[0] $script:GEAR[1]
  Start-Sleep -Milliseconds 1600
  $s = VP-PanelOpen
  VP-Log "opened panel, hdr=$($s.mean)"
  if (-not $s.open) { throw "ABORT: clicking the gear did not open the panel (hdr=$($s.mean))" }
}

function VP-ClosePanel {
  $s = VP-PanelOpen
  if (-not $s.open) { return }
  VP-Click $script:PANEL_X[0] $script:PANEL_X[1]
  Start-Sleep -Milliseconds 1200
  $s = VP-PanelOpen
  VP-Log "closed panel, hdr=$($s.mean)"
  if ($s.open) { throw "ABORT: clicking X did not close the panel (hdr=$($s.mean))" }
}

# Wheel a horizontal strip to one end. Concepts scrolls these strips
# horizontally from a VERTICAL wheel.
function VP-StripEnd([int[]]$at, [int]$dir, [int]$clicks = 18) {
  for ($i = 0; $i -lt $clicks; $i++) {
    [Q]::Wheel($at[0], $at[1], $dir)
    Start-Sleep -Milliseconds 130
  }
  $script:LastPut = @($at[0], $at[1])
  Start-Sleep -Milliseconds 600
}

# A clean full-frame capture: close the panel, park the cursor, shoot, reopen.
# The panel occludes x > 2127, which is where a 3/4 or Side vanishing point
# lives, so the panel MUST be closed for the capture - see the -Clean note.
function VP-CaptureClean([string]$name) {
  VP-ClosePanel
  VP-Put $script:PARK[0] $script:PARK[1]
  Start-Sleep -Milliseconds 800
  VP-Guard "before shot $name"
  [Q]::Shot("$script:VPOUT\$name.png")
  VP-Log "captured $name.png"
  VP-Guard "after shot $name"
}
