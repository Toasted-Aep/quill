# vp9.ps1 - twelfth screen run.  vp8.ps1 re-pathed, plus a STRICT presence
# re-check (Q-Presence) folded into every injection and an input-desktop assert.
# Dot-source.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\tools\vpsweep\q.ps1"
. "$PSScriptRoot\fg.ps1"
. "$PSScriptRoot\top.ps1"

$script:QRoot   = (Resolve-Path "$PSScriptRoot\..").Path
$script:QExe    = "$QRoot\src\Quill\bin\x64\Debug\net8.0-windows10.0.19041.0\Quill.exe"
$script:QData   = "$QRoot\scratchpad\vp9data"
$script:QShots  = "$QRoot\scratchpad\vp9"
$script:QProbe  = "$QRoot\scratchpad\vp9\theme.log"
$script:QPid    = $null

if (-not (Test-Path $QShots)) { New-Item -ItemType Directory -Path $QShots | Out-Null }

Add-Type -Namespace VP9 -Name Desk -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr OpenInputDesktop(uint f, bool inherit, uint access);
[DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetUserObjectInformation(IntPtr h, int i, System.Text.StringBuilder p, int n, out uint len);
'@

function Q-Desktop {
  $h = [VP9.Desk]::OpenInputDesktop(0, $false, 0x0100)
  if ($h -eq [IntPtr]::Zero) { return "<unreachable>" }
  $sb = New-Object System.Text.StringBuilder 256; $n = 0
  [VP9.Desk]::GetUserObjectInformation($h, 2, $sb, 256, [ref]$n) | Out-Null
  [VP9.Desk]::CloseDesktop($h) | Out-Null
  $sb.ToString()
}

# --- the user's real library must never be touched ---------------------------
$script:QReal = 'C:\Users\irony\Documents\Quill\library.json'
function Q-Seal {
  $i = Get-Item $script:QReal
  [pscustomobject]@{
    size  = $i.Length
    mtime = $i.LastWriteTimeUtc.ToString('o')
    sha   = (Get-FileHash $script:QReal -Algorithm SHA256).Hash
  }
}

# STRICT presence: throws on ANY displacement from where we last put the cursor,
# and on the input desktop having moved out from under us.
function Q-Presence {
  $d = Q-Desktop
  if ($d -ne 'Default') { throw "Q-Presence: input desktop is '$d', not Default - readings are worthless" }
  if ($script:LastPut) {
    if ([Q]::Cx() -ne $script:LastPut[0] -or [Q]::Cy() -ne $script:LastPut[1]) {
      throw "Q-Presence: cursor moved on its own - put at $($script:LastPut -join ','), now $([Q]::Cursor())"
    }
  }
  $true
}

function Q-Launch {
  $env:QUILL_DATA_FOLDER = $script:QData
  $env:QUILL_THEME_PROBE = $script:QProbe
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
  return $q.Id
}

# The gate that protects anything else on screen: injected clicks are hit-tested
# by Z-ORDER, so nothing is pressed unless the window UNDER THE CURSOR is Quill.
function Q-Under {
  $q = Get-Process -Name Quill -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $q) { throw "Q-Under: Quill is gone" }
  $u = [Top]::PidUnderCursor()
  if ($u -ne $q.Id) { throw "Q-Under: window under cursor belongs to pid $u, not Quill ($($q.Id))" }
  $true
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
function Q-Safe { Q-Pin; try { Q-Ensure | Out-Null } catch { }; $true }

# Press / release that refuse to fire into anyone else's window AND re-check
# presence first.
function Q-Down { Q-Presence | Out-Null; Q-Under | Out-Null; [Q]::Down() }
function Q-Up   { [Q]::Up() }
function Q-Tap([int]$x, [int]$y, [int]$ms = 70) {
  Put $x $y; Start-Sleep -Milliseconds 260; Q-Down; Start-Sleep -Milliseconds $ms; Q-Up
  Start-Sleep -Milliseconds 260
}

function Shot([string]$name) { [Q]::Shot("$script:QShots\$name.png"); "$script:QShots\$name.png" }
function ShotBox([int]$x, [int]$y, [int]$w, [int]$h, [string]$name) {
  [Q]::ShotRegion($x, $y, $w, $h, "$script:QShots\$name.png"); "$script:QShots\$name.png"
}
function Px([int]$x, [int]$y) { [Q]::Pixel($x, $y) }

function Q-ThemeClear { if (Test-Path $script:QProbe) { Remove-Item $script:QProbe -Force } }
function Q-Theme {
  if (-not (Test-Path $script:QProbe)) { return "<no probe>" }
  Get-Content $script:QProbe | Select-Object -Last 1
}

# contrast between two "#RRGGBB" strings, WCAG, gamma-correct
function Q-Contrast([string]$a, [string]$b) {
  function L([string]$h) {
    $r = [Convert]::ToInt32($h.Substring(1, 2), 16) / 255.0
    $g = [Convert]::ToInt32($h.Substring(3, 2), 16) / 255.0
    $bl = [Convert]::ToInt32($h.Substring(5, 2), 16) / 255.0
    function Lin($v) { if ($v -le 0.04045) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) } }
    0.2126 * (Lin $r) + 0.7152 * (Lin $g) + 0.0722 * (Lin $bl)
  }
  $la = L $a; $lb = L $b
  [Math]::Round((([Math]::Max($la, $lb) + 0.05) / ([Math]::Min($la, $lb) + 0.05)), 2)
}
