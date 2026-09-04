# Phase S - 28 row 3, done properly.  The dial MOVES DOWN with the top bar when
# Text raises the format bar, so the pen wedge and the A cell have different
# coordinates in the two states.  Each flip asserts the resulting state by
# sampling the format-bar row before measuring.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null

$A_raised   = @(329, 435)   # A cell, dial in the NO-format-bar position
$Pen_lowered= @(240, 517)   # pen wedge, dial in the FORMAT-BAR-UP position
$FmtProbeX  = 1400          # inside the format bar's row when it is up
$FmtProbeY  = 40

function State() {
  $p = Px $FmtProbeX $FmtProbeY
  if ($p -eq '#E10619') { 'PEN (no format bar)' } else { "TEXT (format bar up, $p)" }
}

"start state : $(State)"
foreach ($step in @('pen','text','pen','text','pen')) {
  $tgt = if ($step -eq 'text') { $A_raised } else { $Pen_lowered }
  Q-Tap $tgt[0] $tgt[1] | Out-Null
  Start-Sleep -Seconds 2
  $st = State
  $n = "s-$step-$([Guid]::NewGuid().ToString('N').Substring(0,4))"
  Shot $n | Out-Null
  "tap $($tgt -join ',') -> $st   [$n]"
}
"cursor: $([Q]::Cursor())"
