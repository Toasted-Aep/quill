# Phase V - the BottomMenu pill under Theme = Light: resting, hovered, and after
# a FORCED REBUILD (pressing a cell re-runs BuildToolMenu).  Run 11's lesson: a
# pill that does not repaint on a theme change measures "pass" for the wrong
# reason, so never trust the one that was already on screen.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
""
"FULL THEME LOG:"
Get-Content $script:QProbe
""
# resting - cursor parked well away from the pill
Put 700 1300
Start-Sleep -Milliseconds 600
Shot "v01-rest" | Out-Null
"rest      : plate $(Px 1740 1710)"
# hovered
Put 1385 1710
Start-Sleep -Milliseconds 900
Shot "v02-hover" | Out-Null
"hover     : cell  $(Px 1385 1690)"
# forced rebuild: press the Lasso cell (BuildToolMenu re-runs)
Q-Tap 1180 1710 | Out-Null
Start-Sleep -Seconds 2
Put 700 1300
Start-Sleep -Milliseconds 800
Shot "v03-rebuilt" | Out-Null
"rebuilt   : plate $(Px 1740 1710)"
"THEME NOW : $(Q-Theme)"
"cursor: $([Q]::Cursor())"
