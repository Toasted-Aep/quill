# Re-take the Gestures capture after a Windows notification toast contaminated
# the first one.  The toast belonged to the user's own desktop, so that capture
# was deleted rather than committed.
. "$PSScriptRoot\vp10.ps1"
"DESKTOP : $(Q-Desktop)"
Q-Safe | Out-Null
Q-Tap 2482 298 | Out-Null
Start-Sleep -Seconds 2
Shot "n01-gestures" | Out-Null
"retaken; cursor $([Q]::Cursor())  idle $([Q]::Idle())"
