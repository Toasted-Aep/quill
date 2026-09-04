# Phase N - hunt a stock ComboBox (27.6 check 4's third control).
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
Q-Tap 2482 298 | Out-Null           # Gestures tab
Start-Sleep -Seconds 2
Shot "n01-gestures" | Out-Null
Q-Tap 2059 298 | Out-Null           # back to Workspace
Start-Sleep -Seconds 2
Q-Tap 2782 553 | Out-Null           # collapse Artboard so Measurements rises
Start-Sleep -Seconds 2
Shot "n02-measurements" | Out-Null
"cursor: $([Q]::Cursor())"
