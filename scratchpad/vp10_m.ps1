# Phase M - collapse Canvas so the Artboard / Measurements fields rise into
# view: those are where the stock TextBox / ComboBox live (27.6 check 4).
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
Q-Tap 2782 453 | Out-Null          # Canvas chevron - collapse
Start-Sleep -Seconds 2
Shot "m01-canvas-collapsed" | Out-Null
"cursor: $([Q]::Cursor())"
