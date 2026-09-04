# Phase B - open the seeded page, capture the canvas + dial.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
"cursor  : $([Q]::Cursor())  idle $([Q]::Idle())"
Q-Safe | Out-Null

# "Continue: VP10, Sweep, Page 1"
Q-Tap 2035 167
Start-Sleep -Seconds 2
Shot "b01-page" | Out-Null
"after continue: cursor $([Q]::Cursor())"
""
"GEOM:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none>" }
