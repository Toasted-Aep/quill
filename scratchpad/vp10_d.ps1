# Phase D - restart with the dial at the far corner so the half of the wheel
# that was off screen comes into view, then open the wheel and dump geometry.
param([string]$Anchor = 'BottomRight')
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Kill
python "$PSScriptRoot\vp10_dock.py" $Anchor
if ($LASTEXITCODE -ne 0) { throw "dock patch failed" }

if (Test-Path $script:QGeom) { Remove-Item $script:QGeom -Force }
$p = Q-Launch
"launched pid: $p"
Q-Safe | Out-Null
Start-Sleep -Seconds 2
Shot "d01-boot" | Out-Null
# continue into the seeded page
Q-Tap 2035 167 | Out-Null
Start-Sleep -Seconds 2
Shot "d02-page" | Out-Null
""
"GEOM after page open:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none>" }
"cursor: $([Q]::Cursor())"
