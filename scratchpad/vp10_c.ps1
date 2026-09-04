# Phase C - open the COPIC wheel off the dial's dot and dump its geometry.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
"cursor  : $([Q]::Cursor())  idle $([Q]::Idle())"
Q-Safe | Out-Null
"dot px before : $(Px 287 364)"
Q-Tap 287 364
Start-Sleep -Seconds 3
Shot "c01-wheel" | Out-Null
""
"GEOM:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none>" }
