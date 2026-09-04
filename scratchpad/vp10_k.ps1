# Phase K - 27.6 check 4: stock TextBox / Slider / ComboBox in Settings, on
# white paper, must be in their LIGHT theme.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
# Interaction tab
Q-Tap 2280 298 | Out-Null
Start-Sleep -Seconds 2
Shot "k01-interaction" | Out-Null
# Stylus tab
Q-Tap 2648 298 | Out-Null
Start-Sleep -Seconds 2
Shot "k02-stylus" | Out-Null
# back to Workspace, expand Measurements
Q-Tap 2059 298 | Out-Null
Start-Sleep -Seconds 2
Q-Tap 2045 1509 | Out-Null
Start-Sleep -Seconds 2
Shot "k03-measurements" | Out-Null
"cursor: $([Q]::Cursor())"
"THEME : $(Q-Theme)"
