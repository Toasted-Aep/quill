# Phase L - scroll the Settings panel looking for stock TextBox / Slider /
# ComboBox (27.6 check 4).  Scrolling only; nothing is pressed.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
# expand Artboard too, so its fields are in the run
Q-Tap 2045 1362 | Out-Null
Start-Sleep -Seconds 2
Shot "l00-artboard" | Out-Null
Put 2300 1200
for ($i = 1; $i -le 6; $i++) {
  Q-Presence | Out-Null
  [Q]::Wheel(2300, 1200, -4)
  Start-Sleep -Milliseconds 700
  Shot ("l{0:00}-scroll" -f $i) | Out-Null
}
"cursor: $([Q]::Cursor())"
