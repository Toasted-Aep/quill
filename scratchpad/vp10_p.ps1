# Phase P - 27.6 check 3: switch the PAPER to Darkprint with Settings open and
# WITHOUT touching Theme.  The panel must turn #404143 with white text.
#
# Run 11's warning applies: a plate may not repaint on the change, so the panel
# is read TWICE - as it stands after the switch, and again after a forced
# rebuild (tab away and back).  If those disagree, the repaint is the defect.
param([int]$X = 2772, [int]$Y = 718, [string]$Name = 'darkprint')
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null

$s0 = Get-Content "$script:QData\settings.json" -Raw | ConvertFrom-Json
"Theme BEFORE : Settings.Theme=$($s0.Settings.Theme)  Ui.Theme=$($s0.Ui.Theme)"

Q-Tap $X $Y | Out-Null
Start-Sleep -Seconds 3
Shot "p01-$Name-asis" | Out-Null
"THEME PROBE  : $(Q-Theme)"

# force a rebuild of the panel's children, then read it again
Q-Tap 2280 298 | Out-Null            # Interaction
Start-Sleep -Milliseconds 900
Q-Tap 2059 298 | Out-Null            # back to Workspace
Start-Sleep -Seconds 2
Shot "p02-$Name-rebuilt" | Out-Null

$s1 = Get-Content "$script:QData\settings.json" -Raw | ConvertFrom-Json
"Theme AFTER  : Settings.Theme=$($s1.Settings.Theme)  Ui.Theme=$($s1.Ui.Theme)"
$lib = Get-Content "$script:QData\library.json" -Raw | ConvertFrom-Json
"page Paper   : $($lib.Notebooks[0].Sections[0].Pages[0].Paper)"
"page Bg      : $($lib.Notebooks[0].Sections[0].Pages[0].Background)"
"cursor: $([Q]::Cursor())"
