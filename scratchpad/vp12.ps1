# vp12.ps1 - sixteenth screen run. Same harness shape as vp5.ps1, pointed at
# vp12data / vp12 captures. Dot-source.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\tools\vpsweep\q.ps1"
. "$PSScriptRoot\fg.ps1"
. "$PSScriptRoot\top.ps1"

$script:QRoot   = (Resolve-Path "$PSScriptRoot\..").Path
$script:QExe    = "$QRoot\src\Quill\bin\x64\Debug\net8.0-windows10.0.19041.0\Quill.exe"
$script:QData   = "$QRoot\scratchpad\vp12data"
$script:QTrace  = "$QRoot\scratchpad\vp12\trace.log"
$script:QShots  = "$QRoot\scratchpad\vp12"
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

function Q-Under {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  $u = [Top]::PidUnderCursor()
  if ($u -ne $q.Id) { throw "Q-Under: window under cursor belongs to pid $u, not Quill ($($q.Id))" }
  return $true
}

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

function Q-Down { Q-Under | Out-Null; [Q]::Down() }
function Q-Up   { [Q]::Up() }
function Q-Tap([int]$x,[int]$y,[int]$ms = 70) {
  Put $x $y; Start-Sleep -Milliseconds 250; Q-Down; Start-Sleep -Milliseconds $ms; Q-Up
}

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
