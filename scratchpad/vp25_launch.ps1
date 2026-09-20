# Run 25 / 49.8 - launch Quill against the vp25 SCRATCH data folder.
#
# Adapted from w5_launch.ps1 (run 24). Differences:
#   * -KeepLibrary is the DEFAULT here, because the whole point of this run is
#     to compare two hand-seeded layer ORDERS on the glass and nothing in the
#     app can reorder a layer. Wiping the library would wipe the measurement.
#   * The SyncLog guard is built in. LibraryStore.Save calls SyncLog.OnSaved
#     unconditionally and the cursor file lives OUTSIDE the data folder
#     (%LOCALAPPDATA%\Quill\synccursors.json, 18.11), so a scratch launch can
#     still reset the real user's replay offset. Snapshot, then restore.
param(
  [string]$Data = "C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp25data",
  [string]$Out  = "C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp25",
  [string]$Tag  = "a",
  [int]$SettleMs = 3000
)
$ErrorActionPreference = 'Stop'
$root = "C:\Users\irony\Downloads\Quill Gem - Fable\Quill"
. "$root\tools\vpsweep\q.ps1"

# LogonUI is checked SEPARATELY and every time - OpenInputDesktop has lied five
# times, once while the machine auto-locked at ~12 minutes mid-run.
$logon = @(Get-Process -Name LogonUI -ErrorAction SilentlyContinue)
if ($logon.Count -ne 0) { throw "PRESENCE GATE FAILED: LogonUI RUNNING" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null

# --- SyncLog guard (18.11) --------------------------------------------------
$sync = "$env:LOCALAPPDATA\Quill\synccursors.json"
$dev  = "$env:LOCALAPPDATA\Quill\deviceid.txt"
$snap = "$env:TEMP\vp25-syncguard"
New-Item -ItemType Directory -Force $snap | Out-Null
$hs0 = $null; $hd0 = $null
if (Test-Path $sync) { Copy-Item $sync "$snap\s.json" -Force; $hs0 = (Get-FileHash $sync).Hash }
if (Test-Path $dev)  { Copy-Item $dev  "$snap\d.txt"  -Force; $hd0 = (Get-FileHash $dev).Hash }

# Seeded EVERY launch: placement is captured on Closed, so a scratch folder
# otherwise inherits whatever the last run left. Fixed windowed bounds make two
# screenshots comparable pixel for pixel, which is the entire measurement here.
# No BOM - PowerShell's utf8 encoders emit one and the app's JSON reader chokes.
$seed = @{
  Ui = @{
    Theme = "Light"; OledBlack = $false; Accent = "#D97757"
    WinX = 120.0; WinY = 90.0; WinW = 1700.0; WinH = 1180.0
    WinMaximized = $false
    StartMaximised = $false
  }
}
$json = $seed | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $Data "settings.json"), $json, (New-Object System.Text.UTF8Encoding($false)))

$exe = "$root\src\Quill\bin\x64\Debug\net8.0-windows10.0.19041.0\Quill.exe"
foreach ($f in @("Quill.exe","Quill.dll","Quill.runtimeconfig.json","Quill.deps.json")) {
  $p = Join-Path (Split-Path $exe) $f
  if (-not (Test-Path $p)) { throw "MISSING BUILD OUTPUT: $f  (rebuild with --no-incremental)" }
}

$env:QUILL_DATA_FOLDER = $Data
Get-Process Quill -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$libBefore = (Get-FileHash (Join-Path $Data "library.json")).Hash

$p = Start-Process $exe -PassThru
Set-Content -Path "$Out\pid.txt" -Value $p.Id -Encoding ascii
"pid=$($p.Id)  data=$Data"

for ($i = 0; $i -lt 40; $i++) {
  Start-Sleep -Milliseconds 400
  $p.Refresh()
  if ($p.MainWindowHandle -ne 0) { break }
}
Start-Sleep -Milliseconds $SettleMs
$p.Refresh()
"window=$([Q]::FgTitle())  handle=$($p.MainWindowHandle)"
[Q]::Shot("$Out\$Tag-page.png")
"shot -> $Out\$Tag-page.png"

$cl = Join-Path $Data "crash.log"
if (Test-Path $cl) { "--- crash.log ---"; Get-Content $cl } else { "no crash.log" }

"libhash before=$($libBefore.Substring(0,8))  after=$((Get-FileHash (Join-Path $Data 'library.json')).Hash.Substring(0,8))"
