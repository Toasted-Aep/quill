# vp_sweep.ps1 - capture every preset of ONE grid type to its own PNG.
#
#   .\vp_sweep.ps1 -Type 3-Point -Names "3 Point","3/4 Narrow",... [-Clean]
#
# -Clean closes the settings panel for each canvas capture, which costs two
# extra clicks per preset but exposes the whole 2880 px frame. Without it the
# panel stays up and only x < 1680 of the canvas is visible - still enough for
# the convergence measurement, not enough to see a dot at the 3/4 mark.
#
# Guarded at every step: if the cursor moves where we did not put it, or the
# foreground stops being Concepts, the script throws and stops injecting.

param(
  [Parameter(Mandatory=$true)][string]$Type,
  [Parameter(Mandatory=$true)][string[]]$Names,
  [switch]$Clean,
  [int]$MinIdle = 170
)

$ErrorActionPreference = "Stop"
$S = "C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad"
. "$S\q.ps1"; . "$S\vp_lib.ps1"; . "$S\vp_ui.ps1"
$O = $script:VPOUT

# --- page-state probes, all measured not guessed (see 15.5) ---------------
function R-Back    { [W]::RegionMean(1700, 510, 180, 44) }   # >100 => grid editor is open
function R-TypeHdr { [W]::RegionMean(1700, 222, 180, 30) }   # >20  => Grid Type root page
function R-Preset  { [W]::RegionMean(1706, 740, 150, 30) }   # >20  => Preset strip at home
function VP-State {
  $b = R-Back; $t = R-TypeHdr; $p = R-Preset
  return @{ back=[math]::Round($b,1); typehdr=[math]::Round($t,1); preset=[math]::Round($p,1)
            inEditor=($b -gt 100); atRoot=($t -gt 20); presetHome=($p -gt 20) }
}

function VP-GotoRoot {
  $st = VP-State
  if ($st.atRoot) { return }
  if ($st.inEditor) {
    VP-Click $script:BACK[0] $script:BACK[1]
    Start-Sleep -Milliseconds 1500
  }
  # the Grid Type header lives at the very top of the panel's scroll
  for ($i = 0; $i -lt 12; $i++) {
    $st = VP-State
    if ($st.atRoot) { break }
    [Q]::Wheel(1700, 1200, 3); Start-Sleep -Milliseconds 200
  }
  $script:LastPut = @(1700,1200)
  $st = VP-State
  VP-Log "GotoRoot -> back=$($st.back) typehdr=$($st.typehdr)"
  if (-not $st.atRoot) { throw "ABORT: could not reach the Grid Type root page ($($st | ConvertTo-Json -Compress))" }
}

function VP-SelectType([string]$name) {
  VP-GotoRoot
  $idx = [array]::IndexOf($script:TYPES, $name)
  if ($idx -lt 0) { throw "ABORT: unknown grid type '$name'" }
  if ($idx -le 6) {
    VP-StripEnd $script:TYPE_WHEEL 3 8        # rewind the type row
    $x = $script:TYPE_X0 + $script:TYPE_DX * $idx
  } else {
    VP-StripEnd $script:TYPE_WHEEL -3 8       # advance to the end
    # after the row is hard against its right stop the LAST type sits at the
    # right-most slot; count back from there
    $x = $script:TYPE_X0 + $script:TYPE_DX * 6 - $script:TYPE_DX * (8 - $idx)
  }
  [Q]::ShotRegion(1690, 340, 1190, 130, "$O\typerow-$($name -replace '[^A-Za-z0-9]','_').png")
  VP-Log "selecting grid type '$name' (idx $idx) at x=$x"
  VP-Click $x $script:TYPE_Y
  Start-Sleep -Milliseconds 2000
  VP-Guard "type selected $name"
}

function VP-OpenEditor {
  $st = VP-State
  if (-not $st.inEditor) {
    VP-Click $script:EDITGRID[0] $script:EDITGRID[1]
    Start-Sleep -Milliseconds 2000
  }
  $st = VP-State
  if (-not $st.inEditor) { throw "ABORT: Edit Grid did not open the editor ($($st.back))" }
  # bring the Preset section to its home position
  for ($i = 0; $i -lt 14; $i++) {
    $st = VP-State
    if ($st.presetHome) { break }
    [Q]::Wheel(1700, 1200, 3); Start-Sleep -Milliseconds 200
  }
  $script:LastPut = @(1700,1200)
  $st = VP-State
  VP-Log "OpenEditor -> back=$($st.back) preset=$($st.preset)"
  if (-not $st.presetHome) { throw "ABORT: could not bring the Preset strip to its home scroll" }
}

# ---------------------------------------------------------------- run ----
$idle = [Q]::Idle()
if ($idle -lt $MinIdle) { throw "ABORT: idle only ${idle}s - the user is at the machine" }
VP-Log "=== SWEEP $Type ($($Names.Count) presets) clean=$Clean idle=${idle}s ==="

$p = VP-Concepts
$main = $null
foreach ($h in [W]::ByPid([uint32]$p.Id)) { $r = VP-Rect $h; if ($r.W -gt 1000 -and $r.H -gt 800) { $main = $h } }
[W]::Raise($main) | Out-Null
Start-Sleep -Milliseconds 800
if ([Q]::FgTitle() -notlike "*Concepts*") { throw "ABORT: Concepts would not come forward" }
VP-Put $script:PARK[0] $script:PARK[1]; Start-Sleep -Milliseconds 400
VP-Guard "sweep start"

VP-OpenPanel
VP-SelectType $Type
VP-OpenEditor

$safe = { param($n) ($n -replace '[\\/:*?""<>|]','-') }

$station = -1     # -1 = unknown, 0 = rewound, 1 = advanced
for ($i = 0; $i -lt $Names.Count; $i++) {
  $name = $Names[$i]
  $file = "$Type - $(& $safe $name)"

  # position the strip so entry $i is on screen, then compute its x
  if ($i -le 6) {
    if ($station -ne 0) { VP-StripEnd $script:STRIP_WHEEL 3 16; $station = 0 }
    $x = $script:STRIP_X0 + $script:STRIP_DX * $i
  } else {
    if ($station -ne 1) { VP-StripEnd $script:STRIP_WHEEL -3 16; $station = 1 }
    $last = $Names.Count            # + Custom occupies the final slot
    $x = $script:STRIP_X0 + $script:STRIP_DX * 6 - $script:STRIP_DX * ($last - $i)
  }
  VP-Guard "before click $name"
  VP-Log "preset[$i] '$name' -> click x=$x y=$($script:STRIP_Y)"
  VP-Click $x $script:STRIP_Y
  Start-Sleep -Milliseconds 1200
  VP-Guard "after click $name"

  # keep the strip alongside every capture so the PNG is provably labelled
  [Q]::ShotRegion($script:STRIP_REGION[0],$script:STRIP_REGION[1],$script:STRIP_REGION[2],$script:STRIP_REGION[3], "$O\strip - $file.png")

  if ($Clean) {
    VP-CaptureClean $file
    VP-OpenPanel
    Start-Sleep -Milliseconds 600
    $st = VP-State
    if (-not $st.inEditor) { VP-Log "panel reopened outside the editor ($($st.back)) - renavigating"; VP-SelectType $Type; VP-OpenEditor; $station = -1 }
    elseif (-not $st.presetHome) { VP-OpenEditor; $station = -1 }
  } else {
    VP-Put $script:PARK[0] $script:PARK[1]
    Start-Sleep -Milliseconds 500
    VP-Guard "before shot $name"
    [Q]::Shot("$O\$file.png")
    VP-Log "captured '$file.png'"
  }
  VP-Guard "end of $name"
}

VP-Log "=== SWEEP $Type done, idle=$([Q]::Idle())s ==="
