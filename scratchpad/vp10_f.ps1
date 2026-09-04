# Phase F - open the COPIC wheel with the dial at BottomRight.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
"dot px before : $(Px 2596 1351)"
Q-Tap 2596 1351 | Out-Null
Start-Sleep -Seconds 3
Shot "f01-wheel-br" | Out-Null
""
"GEOM:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none>" }
"cursor: $([Q]::Cursor())"
