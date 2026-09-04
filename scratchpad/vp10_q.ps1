# Phase Q - 28: restart on run 11's own page colour (#E10619) with the dial back
# at TopLeft, go fullscreen with the pen, and capture the ChromeBars cluster.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Kill
python "$PSScriptRoot\vp10_paper.py" ""
python "$PSScriptRoot\vp10_dock.py" TopLeft
python "$PSScriptRoot\vp10_bg.py" "#E10619"
if ($LASTEXITCODE -ne 0) { throw "seed failed" }
if (Test-Path $script:QGeom) { Remove-Item $script:QGeom -Force }
$p = Q-Launch
"launched pid: $p"
Q-Safe | Out-Null
Start-Sleep -Seconds 2
Q-Tap 2035 167 | Out-Null            # Continue: VP10, Sweep, Page 1
Start-Sleep -Seconds 3
Shot "q01-windowed" | Out-Null
"page px : $(Px 1600 900)"
# F11 -> fullscreen
Q-Presence | Out-Null
[Q]::Key(0x7A)
Start-Sleep -Seconds 3
Shot "q02-fs-pen" | Out-Null
"after F11: cursor $([Q]::Cursor())"
