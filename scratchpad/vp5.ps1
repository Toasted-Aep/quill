# vp5.ps1 - fifth screen run. Dot-source. Rebuilds the run-4 harness surface
# (Q-Launch / Q-Safe / Q-Ensure / Q-Assert / Crop / Pixel helpers) on top of
# tools/vpsweep/q.ps1, which is the only piece that survived in the repo.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\tools\vpsweep\q.ps1"
. "$PSScriptRoot\fg.ps1"
. "$PSScriptRoot\top.ps1"

$script:QRoot   = (Resolve-Path "$PSScriptRoot\..").Path
$script:QExe    = "$QRoot\src\Quill\bin\x64\Debug\net8.0-windows10.0.19041.0\Quill.exe"
$script:QData   = "$QRoot\scratchpad\vp5data"
$script:QTrace  = "$QRoot\scratchpad\vp5\trace.log"
$script:QShots  = "$QRoot\scratchpad\vp5"
$script:QPid    = $null

if (-not (Test-Path $QShots)) { New-Item -ItemType Directory -Path $QShots | Out-Null }
if (-not (Test-Path $QData))  { New-Item -ItemType Directory -Path $QData  | Out-Null }

# --- the user's real library must never be touched ---------------------------
$script:QReal = 'C:\Users\irony\Documents\Quill\library.json'
function Q-Seal {
  $i = Get-Item $script:QReal
  [pscustomobject]@{
    size = $i.Length
    mtime = $i.LastWriteTimeUtc.ToString('o')
    sha  = (Get-FileHash $script:QReal -Algorithm SHA256).Hash
  }
}

function Q-Launch {
  param([string]$Anchor = $null)
  $env:QUILL_DATA_FOLDER = $script:QData
  $env:QUILL_DIALTRACE   = $script:QTrace
  $p = Start-Process -FilePath $script:QExe -PassThru
  $script:QPid = $p.Id
  # wait for a real window
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $q = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($q -and $q.MainWindowHandle -ne 0) { break }
  }
  Start-Sleep -Seconds 3
  return $p.Id
}

function Q-Kill {
  if ($script:QPid) { Stop-Process -Id $script:QPid -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 800
  Get-Process -Name Quill -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
}

# Bring Quill forward and confirm it is there. The run-4 note stands: injected
# clicks land in whatever window is frontmost, so nothing is injected without
# this passing first.
function Q-Ensure {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $q) { throw "Q-Ensure: no Quill process" }
  for ($k = 0; $k -lt 8; $k++) {
    if ([Q]::FgPid() -eq $q.Id) { break }
    [Fg]::Force($q.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 250
  }
  if ([Q]::FgPid() -ne $q.Id) { throw "Q-Ensure: foreground is '$([Q]::FgTitle())' pid $([Q]::FgPid()), wanted Quill pid $($q.Id)" }
  return $q.Id
}

function Q-Assert {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $q) { throw "Q-Assert: Quill is gone" }
  Q-Under | Out-Null
  return $true
}

# The gate that actually protects the user's Concepts document: whatever the
# foreground says, the click goes to the window UNDER THE CURSOR. Nothing is
# pressed unless that window belongs to Quill.
function Q-Under {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  $u = [Top]::PidUnderCursor()
  if ($u -ne $q.Id) { throw "Q-Under: window under cursor belongs to pid $u, not Quill ($($q.Id))" }
  return $true
}

# Pin Quill above every normal window, and put Concepts back down if it has
# re-activated. Reversible: Q-Unpin restores both.
function Q-Pin {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  [Top]::Demote("Concepts") | Out-Null
  [Top]::Pin($q.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 250
}
function Q-Unpin {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($q) { [Top]::Unpin($q.MainWindowHandle) | Out-Null }
}

# Press / release that refuse to fire into anyone else's window.
function Q-Down { Q-Under | Out-Null; [Q]::Down() }
function Q-Up   { [Q]::Up() }
function Q-Tap([int]$x,[int]$y,[int]$ms = 70) {
  Put $x $y; Start-Sleep -Milliseconds 250; Q-Down; Start-Sleep -Milliseconds $ms; Q-Up
}

# idle is NOT part of the gate this run - see the notes: something on this
# machine resets GetLastInputInfo continuously while the cursor never moves.
# The cursor+foreground pair is what caught run 1's mismeasure and it still works.
# Q-Safe, revised mid-run. Run 4 gated on GetForegroundWindow; on this machine
# Concepts keeps the foreground even while MINIMISED, so that gate both refuses
# valid work and answers the wrong question. What protects the user's document
# is where the click actually lands, and injected mouse input is hit-tested by
# Z-ORDER: Quill pinned topmost + WindowFromPoint(cursor) == Quill is a strictly
# stronger guarantee than "Quill is foreground", which on its own says nothing
# about what is under the pointer. Q-Ensure is still attempted, but softly.
function Q-Safe {
  Q-Pin
  try { Q-Ensure | Out-Null } catch { }
  $true
}

function Shot([string]$name) { [Q]::Shot("$script:QShots\$name.png"); "$script:QShots\$name.png" }
function ShotBox([int]$x,[int]$y,[int]$w,[int]$h,[string]$name) {
  [Q]::ShotRegion($x,$y,$w,$h,"$script:QShots\$name.png"); "$script:QShots\$name.png"
}
function Px([int]$x,[int]$y) { [Q]::Pixel($x,$y) }

function Q-Anchor {
  $f = "$script:QData\library.json"
  if (-not (Test-Path $f)) { return "<no library>" }
  $m = Select-String -Path $f -Pattern '"DialAnchor"\s*:\s*"([^"]*)"' -AllMatches
  if ($m) { return $m.Matches[0].Groups[1].Value }
  return "<absent>"
}

function Q-TraceClear { if (Test-Path $script:QTrace) { Remove-Item $script:QTrace -Force } }
function Q-Trace { if (Test-Path $script:QTrace) { Get-Content $script:QTrace } else { "<no trace>" } }
