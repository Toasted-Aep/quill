# Phase J - 27.6 check 1/2/4: default install (Theme Dark / Manual), Plain White
# paper, Settings open.  Reads the theme probe as well as the screen.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
if (Test-Path $script:QProbe) { Remove-Item $script:QProbe -Force }
if (Test-Path $script:QGeom)  { Remove-Item $script:QGeom  -Force }
$p = Q-Launch
"launched pid: $p"
Q-Safe | Out-Null
Start-Sleep -Seconds 2
Q-Tap 2035 167 | Out-Null            # Continue: VP10, Sweep, Page 1
Start-Sleep -Seconds 2
Shot "j01-plainwhite-page" | Out-Null
"page pixel (mid canvas) : $(Px 1600 900)"
"THEME PROBE (last)      : $(Q-Theme)"
""
"--- live theme, from settings.json (NOT library.json's stale mirror) ---"
$s = Get-Content "$script:QData\settings.json" -Raw | ConvertFrom-Json
"Settings.Theme : $($s.Settings.Theme)"
"Ui.Theme       : $($s.Ui.Theme)"
"library Theme  : $((Get-Content "$script:QData\library.json" -Raw | ConvertFrom-Json).Theme)  <- mirror"
""
# open Settings (the gear in the page toolbar, top right)
Q-Tap 2731 128 | Out-Null
Start-Sleep -Seconds 3
Shot "j02-settings-open" | Out-Null
"after gear: cursor $([Q]::Cursor())"
"THEME PROBE (last)      : $(Q-Theme)"
