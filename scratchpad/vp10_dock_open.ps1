# Re-dock the dial, restart, open the page and the COPIC wheel, dump geometry.
param([Parameter(Mandatory=$true)][string]$Anchor,
      [Parameter(Mandatory=$true)][int]$DotX,
      [Parameter(Mandatory=$true)][int]$DotY,
      [string]$Tag = 'x')
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
Q-Tap 2035 167 | Out-Null          # Continue: VP10, Sweep, Page 1
Start-Sleep -Seconds 2
Shot "$Tag-page" | Out-Null
"dot px before : $(Px $DotX $DotY)"
Q-Tap $DotX $DotY | Out-Null
Start-Sleep -Seconds 3
Shot "$Tag-wheel" | Out-Null
""
"GEOM:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none>" }
"cursor: $([Q]::Cursor())"
