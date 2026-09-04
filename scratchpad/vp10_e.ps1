# Phase E - find the dial at its new dock, open the wheel, dump geometry.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
Shot "e01-page" | Out-Null
"page shot taken; geom so far:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none>" }
