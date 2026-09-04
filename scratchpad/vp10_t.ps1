# Phase T - 28 row 4: fullscreen with the pen, the strip reserve settled at 144,
# and the Measurement panel opened for the FIRST time this process.  Its info
# glyph must land flush at DIP 1266.0..1279.5, not 1410.0..1423.5.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
"format-bar row px : $(Px 1400 40)   (#E10619 = pen, no format bar)"
Shot "t00-before" | Out-Null
# the zoom readout, "100%", in the ChromeBars cluster
Q-Tap 1929 61 | Out-Null
Start-Sleep -Seconds 3
Shot "t01-measure-first-open" | Out-Null
"cursor: $([Q]::Cursor())"
