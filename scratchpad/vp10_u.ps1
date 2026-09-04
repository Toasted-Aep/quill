# Phase U - the standing 2.54:1 BottomMenu hover under Theme = Light.
# CONFIRM, do not fix.  Built fresh under Light so no plate can be stale.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Kill
python "$PSScriptRoot\vp10_theme.py" Light
if ($LASTEXITCODE -ne 0) { throw "theme patch failed" }
$p = Q-Launch
"launched pid: $p"
Q-Safe | Out-Null
Start-Sleep -Seconds 2
Q-Tap 2035 167 | Out-Null            # Continue: VP10, Sweep, Page 1
Start-Sleep -Seconds 3
Shot "u01-page-light" | Out-Null
$s = Get-Content "$script:QData\settings.json" -Raw | ConvertFrom-Json
"live theme : Settings.Theme=$($s.Settings.Theme)  Ui.Theme=$($s.Ui.Theme)"
"THEME PROBE: $(Q-Theme)"
# the dial's mouse/select tool - the lasso cell
Q-Tap 401 212 | Out-Null
Start-Sleep -Seconds 2
Shot "u02-mousetool" | Out-Null
"cursor: $([Q]::Cursor())"
