# Phase R - 28 row 3: with the format bar up the ChromeBars cluster must sit
# +144 DIP right of the pen case, and the fold must flip LIVE in both directions
# without ever leaving fullscreen.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
$TextA = @(329, 435)      # the dial's A cell
$Pen   = @(240, 437)      # a pen wedge on the same dial

function Flip([string]$to, [string]$name) {
  $p = if ($to -eq 'text') { $TextA } else { $Pen }
  Q-Tap $p[0] $p[1] | Out-Null
  Start-Sleep -Seconds 2
  Shot $name | Out-Null
  "$name : tapped $to at $($p -join ',')"
}

Flip text 'r01-fs-text'
Flip pen  'r02-fs-pen'
Flip text 'r03-fs-text'
Flip pen  'r04-fs-pen'
Flip text 'r05-fs-text'
"cursor: $([Q]::Cursor())"
