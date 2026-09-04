# Phase O - reach Darkprint in the Background swatch row.  Settings stays OPEN
# and Theme is NOT touched: 27.6 check 3 is a page-only move.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
Q-Tap 2059 298 | Out-Null            # Workspace tab
Start-Sleep -Seconds 2
Q-Tap 2782 453 | Out-Null            # expand Canvas
Start-Sleep -Seconds 2
Shot "o00-canvas-open" | Out-Null
Put 2300 718
for ($i = 1; $i -le 5; $i++) {
  Q-Presence | Out-Null
  [Q]::Wheel(2300, 718, -4)
  Start-Sleep -Milliseconds 700
}
Shot "o01-paper-row-scrolled" | Out-Null
"cursor: $([Q]::Cursor())"
"THEME : $(Q-Theme)"
